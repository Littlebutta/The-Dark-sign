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
/// Fallback "weapon-arts": DARKSIGN_WEAPON_ARTS=nohks (no hks; stance arts become a one-shot of their light attack),
/// 0 (off: ported weapons keep their template's Elden Ring Ash of War, as before).
/// </summary>
public static partial class MovesetPort
{
    public static readonly string ArtsMode = (Environment.GetEnvironmentVariable("DARKSIGN_WEAPON_ARTS") ?? "").ToLowerInvariant();
    private static bool ArtsEnabled => ArtsMode != "0";
    private static bool ArtsWithHks => ArtsMode != "nohks";

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

    private sealed record ArtJob(int Ds3Section, int ClassSection, bool Stance, int Ds3Start, List<WeaponPlan> Weapons);

    public sealed record ArtResult(int Arts, int Weapons, Dictionary<int, string> Names, List<int> StanceIds);

    /// <summary>The DS3 art sections whose effects and sounds the arts need (so they are ported with the movesets').</summary>
    private static IEnumerable<int> ArtSections(IEnumerable<WeaponPlan> ports, PARAM ds3Weapons) => !ArtsEnabled ? []
        : ports.Select(p => Convert.ToInt32(ds3Weapons[p.Ds3Id]!["spAtkCategory"].Value)).Where(s => s > 0).Distinct();

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
        // 300-398 first (Elden Ring's own DLC range), then 170-254: c0000.hks gives both the plain one-shot by default.
        var free = new Queue<int>(Enumerable.Range(300, 99).Concat(Enumerable.Range(170, 85)).Where(id => id is not (196 or 305 or 357 or 399)
            && !usedIds.Contains(id) && !taeSections.Contains(600 + id) && !clipsBySection.ContainsKey(600 + id)));

        // One art per DS3 art section; weapons whose DS3 art has no skill animations (talismans, bows) are left alone.
        var jobs = new List<ArtJob>();
        foreach (var g in ports.GroupBy(p => Convert.ToInt32(ds3Weapons[p.Ds3Id]!["spAtkCategory"].Value)).Where(g => g.Key > 0))
        {
            if (ds3Tae(g.Key) is not { } artTae)
                continue;
            int cls = Convert.ToInt32(ds3Weapons[g.First().Ds3Id]!["wepmotionCategory"].Value);
            var skill = artTae.Animations.Select(a => (int)a.ID).Where(id => id is >= 36000 and < 38000 && id / 100 != 369).Order().ToList();
            if (skill.Count == 0)
                continue;
            bool stance = skill.Contains(36200);
            // One-shot: the first move of the skill (its x00 animation). Without the hks, a stance plays its light attack.
            int start = stance ? 36230 : skill.FirstOrDefault(id => id % 100 == 0, skill[0]);
            jobs.Add(new ArtJob(g.Key, cls, stance && ArtsWithHks, start, g.ToList()));
        }
        jobs = jobs.OrderByDescending(j => j.Weapons.Any(p => p.Ds3Id == 10180000)).ThenByDescending(j => j.Weapons.Count).ThenBy(j => j.Ds3Section).ToList();
        Console.WriteLine($"arts: {jobs.Count} DS3 weapon arts with skill animations ({jobs.Count(j => j.Stance)} stances), {free.Count} free art IDs");

        var ds3Captions = new Dictionary<int, string>();
        var names = new Dictionary<int, string>();
        var stanceIds = new List<int>();
        int arts = 0, weapons = 0, nextRow = FirstArtRow;
        foreach (var job in jobs)
        {
            var label = string.Join(", ", job.Weapons.Select(p => p.Name).Take(3)) + (job.Weapons.Count > 3 ? $" +{job.Weapons.Count - 3}" : "");
            if (free.Count == 0)
            {
                Console.WriteLine($"arts: skipped {label} (DS3 a{job.Ds3Section}): no free art ID left, keeps its Elden Ring Ash of War");
                continue;
            }
            // The template art: an Elden Ring stance art for stances; for one-shots the template weapon's own art when
            // it has a one-shot clip, else Lion's Claw's section (600).
            var templateWeapon = erWeapons[job.Weapons[0].TemplateId!.Value]!;
            int templateRowId = Convert.ToInt32(templateWeapon["swordArtsParamId"].Value);
            var templateArtRow = artParam[templateRowId] ?? artParam.Rows.First(r => r.ID > 10);
            int templateSection = job.Stance
                ? StanceTemplates.FirstOrDefault(s => clipsBySection.TryGetValue(s, out var ids) && ids.Contains(40050) && taeSections.Contains(s), -1)
                : clipsBySection.TryGetValue(600 + ArtId(templateArtRow), out var own) && own.Contains(40000) && taeSections.Contains(600 + ArtId(templateArtRow))
                    ? 600 + ArtId(templateArtRow) : 600;
            if (templateSection < 0 || BinderLeaf(erMain, $"a{templateSection}.tae") is not { } templateTaeFile)
            {
                Console.WriteLine($"arts: skipped {label}: no Elden Ring art to start from");
                continue;
            }
            int artId = free.Peek(), section = 600 + artId;
            var map = job.Stance ? StanceMap : [(40000, job.Ds3Start), (40005, job.Ds3Start)];
            var templateIds = clipsBySection[templateSection];
            var ds3Taes = new List<(int Section, TAE? Tae)> { (job.Ds3Section, ds3Tae(job.Ds3Section)), (job.ClassSection, ds3Tae(job.ClassSection)) }
                .Where(t => t.Tae is not null).ToList();
            try
            {
                TAE.ValidateEventBank = false;
                var templateTae = TAE.Read(templateTaeFile.Bytes);
                var tae = TAE.Read(templateTaeFile.Bytes);
                tae.ID = 2000 + section;
                var erEventLengths = templateTae.Animations.SelectMany(a => a.Events).GroupBy(e => e.Type)
                    .ToDictionary(g => g.Key, g => g.First().GetParameterBytes(false).Length);
                var templateGroups = templateTae.Animations.SelectMany(a => a.Events).Where(e => e.Group is not null)
                    .GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.First().Group!);
                var stretches = new Dictionary<int, MoveTiming.Stretch>();
                var kept = new List<TAE.Animation>();
                foreach (var (erId, ds3Id) in map)
                {
                    var anim = tae.Animations.FirstOrDefault(a => a.ID == erId);
                    if (anim is null || !templateIds.Contains(erId) || FindDs3(ds3Taes, ds3Main, ds3Id) is not { } source
                        || ds3Hkx.Read(source.HkxName) is not { } ds3Bytes)
                        continue;
                    var template = ErAnimFile(erArchives, $"a{templateSection:D3}_{erId:D6}")
                                   ?? ErAnimFile(erArchives, AnimSourceName(anim, templateSection, erId))
                                   ?? ErAnimFile(erArchives, "a000_000000")!;
                    var (hkx, _, erDuration) = AnimPort.ConvertTimed(ds3Bytes, ds3Skeleton, erSkeleton, template.Bytes, template.Compendium, AnimPort.Spline);
                    var newName = $"a{section:D3}_{erId:D6}";
                    AddFile(erAnims, newName + ".hkx", hkx);
                    anim.MiniHeader = new TAE.Animation.AnimMiniHeader.Standard();
                    anim.AnimFileName = newName + ".hkt";
                    float ds3Duration = AnimPort.Duration(ds3Bytes);
                    anim.Events = MergeEvents(source.Events, anim, templateGroups, erEventLengths, effectEvents, effects, soundPort, ds3Duration, out var stretch);
                    stretches[erId] = stretch;
                    timing.Add(TimingLine(section, label, erId, source, ds3Duration, erDuration, stretch));
                    kept.Add(anim);
                }
                if (kept.Count == 0 || (job.Stance && !kept.Any(a => a.ID == 40050)) || (!job.Stance && !kept.Any(a => a.ID == 40000)))
                {
                    Console.WriteLine($"arts: skipped {label} (DS3 a{job.Ds3Section}): none of its skill animations fit Elden Ring a{templateSection}");
                    continue;
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
                        if (!m.Success || int.Parse(m.Groups[1].Value) != templateSection)
                            continue;
                        var name = $"a{section:D3}_{m.Groups[2].Value}";
                        if (!internalIds.TryGetValue(name, out var internalId))
                            internalIds[name] = internalId = checked((short)nextInternalId++);
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

                // The Ash of War row: the template weapon's, with the new art ID and DS3's FP cost.
                while (artParam[nextRow] is not null)
                    nextRow += 10;
                var row = new PARAM.Row(nextRow, "", artParam.AppliedParamdef);
                for (int i = 0; i < row.Cells.Count; i++)
                    row.Cells[i].Value = templateArtRow.Cells[i].Value is byte[] b ? b.Clone() : templateArtRow.Cells[i].Value;
                SetArtId(row, artId);
                var (fp, fpLight, fpHeavy, ds3ArtName) = Ds3Art(ds3, ds3Weapons, job.Weapons[0].Ds3Id);
                if (fp >= 0)
                    SetCell(row, "useMagicPoint_L2", fp);
                if (job.Stance)
                {
                    // DS3 charges the stance's follow-ups separately (FPCostLight, FpCostStrong; -1 = the skill's cost).
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
                if (job.Stance)
                    stanceIds.Add(artId);
                free.Dequeue();
                arts++;
                weapons += job.Weapons.Count;
                Console.WriteLine($"arts: {label} -> art {artId} (a{section}, SwordArtsParam {nextRow} '{names[nextRow]}', FP {fp}/{fpLight}/{fpHeavy}) from DS3 a{job.Ds3Section} " +
                                  $"{(job.Stance ? "stance" : $"one-shot {job.Ds3Start}")} on Elden Ring a{templateSection}: {kept.Count} moves, {registered} behaviour clips");
                nextRow += 10;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Console.WriteLine($"arts: skipped {label}: {e.GetType().Name}: {e.Message}");
            }
        }
        if (stanceIds.Count > 0)
            WriteHks(ctx, stanceIds);
        else if (File.Exists(HksPath(ctx)))
            File.Delete(HksPath(ctx));
        Console.WriteLine($"arts: {arts} DS3 weapon arts for {weapons} weapons ({stanceIds.Count} stances{(stanceIds.Count > 0 ? ", c0000.hks written" : "")})");
        return new ArtResult(arts, weapons, names, stanceIds);
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
        int Get(string field) => row[field]?.Value is { } v and not byte[] ? Convert.ToInt32(v) : -1;
        return (Get("FPCost"), Get("FPCostLight"), Get("FpCostStrong"), row.Name);
    }

    /// <summary>"Skill: Spin Sweep" from a DS3 weapon description, if it names one.</summary>
    internal static string? SkillName(string? caption)
    {
        if (caption is null)
            return null;
        var m = System.Text.RegularExpressions.Regex.Match(caption, @"Skill:\s*([^\r\n]+)");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static string HksPath(PorterContext ctx) => ctx.ModPath("action", "script", "c0000.hks");

    /// <summary>ividyon's c0000.hks with the ported stance arts added to IsStanceArts.</summary>
    private static void WriteHks(PorterContext ctx, IReadOnlyList<int> stanceIds)
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
            var text = PatchHks(File.ReadAllText(cache), stanceIds);
            Directory.CreateDirectory(Path.GetDirectoryName(HksPath(ctx))!);
            File.WriteAllText(HksPath(ctx), text, new System.Text.UTF8Encoding(false));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Without the hks the stance arts play as Elden Ring's default one-shot (their stance start only).
            Console.WriteLine($"arts: c0000.hks not written, stance arts won't hold their stance ({e.GetType().Name}: {e.Message})");
        }
    }

    internal static string PatchHks(string hks, IReadOnlyList<int> stanceIds)
    {
        const string fn = "function IsStanceArts(arts_id)";
        int at = hks.IndexOf(fn, StringComparison.Ordinal);
        int list = at < 0 ? -1 : hks.IndexOf("local aow_list = {", at, StringComparison.Ordinal);
        if (list < 0)
            throw new InvalidDataException("c0000.hks has no IsStanceArts list");
        int insert = hks.IndexOf('\n', list) + 1;
        var lines = string.Concat(stanceIds.Select(id => $"        {id}, -- The Dark Sign: DS3 stance art\n"));
        return hks[..insert] + lines + hks[insert..];
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
        Check(MovesetPort.SkillName("A great scythe.\n\nSkill: Spin Sweep\nSpin the scythe.") == "Spin Sweep", "skill name read from a DS3 description");
        Check(MovesetPort.SkillName("No skill here.") is null, "no skill name when the description names none");
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
        Console.WriteLine(failures == 0 ? "arts-selftest: all checks passed" : $"arts-selftest: {failures} failed");
        return failures == 0 ? 0 : 1;
    }
}
