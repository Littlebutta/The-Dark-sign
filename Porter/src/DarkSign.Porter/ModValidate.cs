using System.Text;
using System.Text.RegularExpressions;
using HKLib.hk2018;
using HKLib.Serialization.hk2018.Binary;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// validate-mod: offline checks of the BUILT mod (Mods\ER) that catch broken output before anyone launches the game.
/// Every reference a ported weapon makes is followed into the mod's own files: its row's params, its moveset section's
/// TAE, animations and behaviour clips, the sounds and effects its events play, and its model. Runs at the end of
/// port-weapons too. Writes Work\reports\validate-mod.txt; exit code 1 when any ERROR was found.
/// </summary>
public static partial class ModValidate
{
    public sealed class Report
    {
        private readonly StringBuilder _text = new();
        public int Errors { get; private set; }
        public int Warnings { get; private set; }
        public List<string> ErrorLines { get; } = [];

        public void Error(string line) { Errors++; ErrorLines.Add(line); _text.AppendLine("ERROR " + line); }
        public void Warn(string line) { Warnings++; _text.AppendLine("WARN  " + line); }
        public void Info(string line) => _text.AppendLine("      " + line);
        public override string ToString() => _text.ToString();
    }

    [GeneratedRegex(@"^a(\d{2,3})_(\d{6})")]
    private static partial Regex AnimName();

    public static int Run(PorterContext ctx)
    {
        var report = Check(ctx);
        var path = ctx.WorkPath("reports", "validate-mod.txt");
        File.WriteAllText(path, report.ToString());
        Console.WriteLine($"validate: {report.Errors} errors, {report.Warnings} warnings (full report: {path})");
        foreach (var line in report.ErrorLines.Take(15))
            Console.WriteLine("  ERROR " + line);
        if (report.ErrorLines.Count > 15)
            Console.WriteLine($"  ... {report.ErrorLines.Count - 15} more in the report");
        return report.Errors > 0 ? 1 : 0;
    }

    /// <summary>
    /// The icon menu files must be a whole set in both qualities (a half set made the DS3 icons vanish on 2026-11-07),
    /// and every ported weapon's icon must be on one of the DS3 sheet layouts in each.
    /// </summary>
    private static void CheckIcons(Report r, string modDir, HashSet<int> icons)
    {
        if (IconPort.SetProblem(modDir) is { } problem)
        {
            r.Error($"icons: {problem}; the game mixes its own menus with the mod's and DS3 icons go missing (rebuild with Elden Ring closed)");
            return;
        }
        foreach (var quality in new[] { "hi", "low" })
        {
            var file = Path.Combine(modDir, "menu", quality, "01_common.sblytbnd.dcx");
            if (!File.Exists(file))
            {
                if (icons.Count > 0)
                    r.Warn($"icons: no menu\\{quality} icon files; ported weapons show no icons");
                continue;
            }
            BND4 bnd;
            try { bnd = BND4.Read(DCX.Decompress(File.ReadAllBytes(file))); }
            catch (Exception e) { r.Error($"icons: menu\\{quality}\\01_common.sblytbnd.dcx does not open: {e.GetType().Name}: {e.Message}"); continue; }
            var listed = new HashSet<int>();
            foreach (var layout in bnd.Files.Where(f => ItemText.Leaf(f.Name).StartsWith("SB_Icon_DS3_", StringComparison.Ordinal)))
                foreach (Match m in IconName().Matches(Encoding.UTF8.GetString(layout.Bytes)))
                    listed.Add(int.Parse(m.Groups[1].Value));
            var missing = icons.Where(i => !listed.Contains(i)).Order().ToList();
            if (missing.Count > 0)
                r.Error($"icons: {missing.Count} ported weapon icon(s) are not on a DS3 sheet in menu\\{quality} ({string.Join(", ", missing.Take(10))}); those weapons show no icon");
            else
                r.Info($"icons: menu\\{quality} lists all {icons.Count} ported weapon icons");
        }
    }

    [GeneratedRegex(@"MENU_ItemIcon_(\d{5})\.png")]
    private static partial Regex IconName();

    public static Report Check(PorterContext ctx)
    {
        var r = new Report();
        string modDir = Path.GetDirectoryName(ctx.ModPath("regulation.bin"))!;
        if (!File.Exists(Path.Combine(modDir, "regulation.bin")))
        {
            r.Error("Mods\\ER\\regulation.bin is missing: nothing was built");
            return r;
        }
        var mod = ParamStore.Load(PorterGame.EldenRing, modDir, ctx.Data);
        var weapons = mod.Get("EquipParamWeapon")!;
        var ported = weapons.Rows.Where(row => row.ID >= WeaponPort.FirstNewId && row.ID < 90_000_000).ToList();
        var bases = ported.Where(row => row.ID % 10000 == 0).ToList();
        r.Info($"{bases.Count} ported weapons ({ported.Count} rows with affinities and upgrades)");
        if (bases.Count == 0)
            r.Warn("no ported weapon rows (IDs 80000000+) in the mod's regulation");

        // 1. Every file the mod ships must at least open.
        var binders = new Dictionary<string, BND4>(StringComparer.OrdinalIgnoreCase);
        BND4? Binder(params string[] parts)
        {
            var file = Path.Combine([modDir, .. parts]);
            var key = Path.GetRelativePath(modDir, file);
            if (binders.TryGetValue(key, out var known))
                return known;
            if (!File.Exists(file))
                return null;
            try { return binders[key] = BND4.Read(DCX.Decompress(File.ReadAllBytes(file))); }
            catch (Exception e) { r.Error($"{key} does not open: {e.GetType().Name}: {e.Message}"); return null; }
        }

        // 2. Params each ported row points at.
        var sections = new Dictionary<int, List<int>>(); // moveset section -> base rows using it
        var soundOffsets = new Dictionary<int, int>();
        var hitVariations = new HashSet<int>();
        foreach (var row in ported)
        {
            int section = Field(row, MovesetPort.UseWeaponMotionCategory ? "wepmotionCategory" : "spAtkcategory");
            bool isBase = row.ID % 10000 == 0;
            if (isBase && section >= 90)
            {
                if (!sections.TryGetValue(section, out var list))
                    sections[section] = list = [];
                list.Add(row.ID);
                soundOffsets[section] = Math.Max(soundOffsets.GetValueOrDefault(section), Field(row, "wepSeIdOffset"));
            }
            if (!isBase)
                continue;
            int icon = Field(row, "iconId");
            if (icon < WeaponPort.FirstNewIconId || icon > ushort.MaxValue)
                r.Error($"weapon {row.ID}: iconId {icon} outside {WeaponPort.FirstNewIconId}-65535 (Elden Ring's 16-bit icon IDs; lower ones clash with chrmake)");
            int model = Field(row, "equipModelId");
            if (model >= WeaponPort.FirstNewModelId && !File.Exists(Path.Combine(modDir, "parts", $"wp_a_{model:D4}.partsbnd.dcx")))
                r.Error($"weapon {row.ID}: model wp_a_{model:D4}.partsbnd.dcx is not in the mod (the weapon would be invisible)");
            Points(r, mod, row, "swordArtsParamId", "SwordArtsParam");
            Points(r, mod, row, "absorpParamId", "WepAbsorpPosParam");
            Points(r, mod, row, "originEquipWep", "EquipParamWeapon");
            int variation = Field(row, "behaviorVariationId");
            if (variation > 0 && mod.Get("BehaviorParam_PC") is { } behaviour
                && !behaviour.Rows.Any(b => b.ID >= variation * 1000 && b.ID < variation * 1000 + 1000))
                r.Error($"weapon {row.ID}: behaviorVariationId {variation} has no BehaviorParam_PC rows ({variation * 1000}-{variation * 1000 + 999}); its hits would do nothing");
            else if (variation > 0)
                hitVariations.Add(variation);
        }
        CheckIcons(r, modDir, bases.Select(row => Field(row, "iconId")).ToHashSet());
        // Ported hits need a hitbox: an attack row with a sphere/capsule (radius > 0 on a dummy poly). 1.0.183 copied
        // DS3 attack rows with radius 0 and DS3 weapons hit nothing (enemies or walls).
        if (mod.Get("BehaviorParam_PC") is { } behaviours && mod.Get("AtkParam_Pc") is { } attacks)
        {
            int checkedHits = 0, empty = 0;
            foreach (var b in behaviours.Rows.Where(b => hitVariations.Contains(b.ID / 1000) && Field(b, "refType") == 0))
            {
                if (attacks[Field(b, "refId")] is not { } atk)
                    continue;
                checkedHits++;
                bool hitbox = Enumerable.Range(0, 16).Any(i => atk[$"hit{i}_Radius"] is { } rad && Convert.ToSingle(rad.Value) > 0
                    && atk[$"hit{i}_DmyPoly1"] is { } dmy && Convert.ToInt32(dmy.Value) >= 0);
                if (!hitbox && ++empty <= 20)
                    r.Error($"behaviour {b.ID}: attack {atk.ID} has no hitbox (every hit radius 0 or no dummy poly); this swing hits nothing");
            }
            if (empty > 20)
                r.Error($"... {empty - 20} more ported swings without a hitbox");
            Console.WriteLine($"validate: {checkedHits - empty}/{checkedHits} ported weapon hits have a hitbox");

            // Where the hit spheres sit: the dummy polys the attack rows name against the dummy polys the weapon's own
            // model (ported from DS3) carries. The games place weapon hit dummies as 10000 + dummy (right hand) /
            // 11000 + dummy (left); a weapon whose attack rows name dummies its model doesn't have swings without
            // hitting anything (1.0.221: DS3 weapons did no damage while their rows passed the radius check above).
            int reported = 0, missing = 0;
            foreach (var row in bases)
            {
                int variation = Field(row, "behaviorVariationId"), model = Field(row, "equipModelId");
                if (variation <= 0 || model < WeaponPort.FirstNewModelId)
                    continue;
                var parts = Binder("parts", $"wp_a_{model:D4}.partsbnd.dcx");
                if (parts is null)
                    continue;
                var modelDummies = new SortedSet<int>();
                foreach (var f in parts.Files.Where(f => FLVER2.Is(f.Bytes)))
                    try { modelDummies.UnionWith(FLVER2.Read(f.Bytes).Dummies.Select(d => (int)d.ReferenceID)); }
                    catch (Exception e) when (e is InvalidDataException or EndOfStreamException) { }
                var hitDummies = new SortedSet<int>();
                foreach (var b in behaviours.Rows.Where(b => b.ID / 1000 == variation && Field(b, "refType") == 0))
                    if (attacks[Field(b, "refId")] is { } atk)
                        for (int i = 0; i < 16; i++)
                            if (atk[$"hit{i}_Radius"] is { } rad && Convert.ToSingle(rad.Value) > 0)
                                foreach (var field in new[] { $"hit{i}_DmyPoly1", $"hit{i}_DmyPoly2" })
                                    if (atk[field] is { } dmy && Convert.ToInt32(dmy.Value) is >= 0 and var v)
                                        hitDummies.Add(v);
                if (hitDummies.Count == 0)
                    continue;
                bool OnModel(int v) => modelDummies.Contains(v) || modelDummies.Contains(v % 1000) || (v >= 10000 && modelDummies.Contains(v - 10000)) || (v >= 11000 && modelDummies.Contains(v - 11000));
                var found = hitDummies.Where(OnModel).ToList();
                var line = $"weapon {row.ID} (wp_a_{model:D4}): attack dummy polys {string.Join(" ", hitDummies)}; model dummy polys {string.Join(" ", modelDummies.Take(24))}{(modelDummies.Count > 24 ? " ..." : "")}; {found.Count}/{hitDummies.Count} on the model";
                if (found.Count == 0)
                {
                    missing++;
                    if (missing <= 10)
                        r.Warn(line + " (its hit spheres have nowhere to sit)");
                }
                else if (reported++ < 8)
                    r.Info(line);
            }
            if (missing > 10)
                r.Warn($"... {missing - 10} more ported weapons whose attack dummy polys are not on their model");
            Console.WriteLine($"validate: {missing} ported weapons name hit dummy polys their model doesn't carry");
        }
        if (!MovesetPort.UseWeaponMotionCategory)
            foreach (var row in bases.Where(row => Field(row, "spAtkcategory") is > 0 and < 50 or > 999))
                r.Error($"weapon {row.ID}: spAtkcategory {Field(row, "spAtkcategory")} outside 50-999");

        // 3. Movesets: TAE, animations, imports, event groups, behaviour clips.
        var main = Binder("chr", "c0000.anibnd.dcx");
        var anims = Binder("chr", "c0000_a9x.anibnd.dcx");
        if (sections.Count > 0 && (main is null || anims is null))
            r.Error("weapons point at ported movesets but chr\\c0000.anibnd.dcx or c0000_a9x.anibnd.dcx is missing");
        foreach (var (name, binder) in binders)
            if (binder.Files.Zip(binder.Files.Skip(1)).FirstOrDefault(p => p.First.ID >= p.Second.ID) is { First: not null } bad)
                r.Error($"{name}: binder IDs out of order at {bad.First.ID} >= {bad.Second.ID} (a binder entry out of ID order hung the game at load)");

        var portedSounds = new List<(int Section, int Animation, int Kind, int Id)>();
        var portedEffects = new List<(int Section, int Animation, int Id)>();
        if (main is not null && anims is not null)
        {
            TAE.ValidateEventBank = false;
            var taes = main.Files.Where(f => f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase))
                .Select(f => (Leaf: ItemText.Leaf(f.Name), File: f))
                .Where(x => int.TryParse(x.Leaf[1..^4], out _))
                .GroupBy(x => int.Parse(x.Leaf[1..^4])).ToDictionary(g => g.Key, g => g.First().File);
            var animIds = anims.Files.Select(f => f.ID).ToHashSet();
            var taeCache = new Dictionary<int, TAE?>();
            TAE? Tae(int section) => taeCache.TryGetValue(section, out var t) ? t
                : taeCache[section] = taes.TryGetValue(section, out var f) ? TAE.Read(f.Bytes) : null;

            foreach (var (section, users) in sections.OrderBy(s => s.Key))
            {
                var tae = Tae(section);
                if (tae is null)
                {
                    r.Error($"section a{section}: no a{section}.tae in the mod's c0000.anibnd, used by weapon(s) {string.Join(", ", users.Take(5))}");
                    continue;
                }
                int own = 0, imports = 0, ungrouped = 0;
                foreach (var anim in tae.Animations)
                {
                    switch (anim.MiniHeader)
                    {
                        case TAE.Animation.AnimMiniHeader.Standard:
                            own++;
                            int id = checked(1_000_000_000 + section * 1_000_000 + (int)anim.ID);
                            if (!animIds.Contains(id))
                                r.Error($"a{section:D3}_{anim.ID:D6}: TAE plays its own animation but c0000_a9x has no file with ID {id}");
                            break;
                        case TAE.Animation.AnimMiniHeader.ImportOtherAnim imp:
                            imports++;
                            int fromSection = imp.ImportFromAnimID / 1_000_000, fromAnim = imp.ImportFromAnimID % 1_000_000;
                            if (fromSection != section && Tae(fromSection) is { } from && !from.Animations.Any(a => a.ID == fromAnim))
                                r.Error($"a{section:D3}_{anim.ID:D6}: imports {imp.ImportFromAnimID}, which a{fromSection}.tae doesn't have");
                            else if (Tae(fromSection) is null)
                                r.Warn($"a{section:D3}_{anim.ID:D6}: imports {imp.ImportFromAnimID} from section {fromSection}, which isn't in the mod's c0000.anibnd");
                            break;
                    }
                    foreach (var e in anim.Events)
                    {
                        if (e.Group is null)
                            ungrouped++;
                        if (e.StartTime < 0 || e.EndTime < e.StartTime)
                            r.Error($"a{section:D3}_{anim.ID:D6}: event type {e.Type} has times {e.StartTime}-{e.EndTime}");
                        if (SoundPort.Sound(e) is { } s && s.Id >= SoundPort.IdBase)
                            portedSounds.Add((section, (int)anim.ID, s.Kind, s.Id));
                        else if (e.Type is >= 95 and <= 123) // SpawnFFX family (DSAnimStudio's Elden Ring TAE template)
                        {
                            var b = e.GetParameterBytes(false);
                            for (int p = 0; p + 4 <= b.Length; p += 4)
                                if (BitConverter.ToInt32(b, p) is >= FxrPort.IdBase and < 1_000_000_000 and var fx)
                                    portedEffects.Add((section, (int)anim.ID, fx));
                        }
                    }
                }
                if (ungrouped > 0)
                    r.Warn($"a{section}: {ungrouped} events outside an event group (Elden Ring skips them)");
                r.Info($"a{section}: {own} own animations, {imports} imported, weapons {string.Join(", ", users.Take(4))}{(users.Count > 4 ? " ..." : "")}");
            }
            CheckBehaviour(r, Binder("chr", "c0000.behbnd.dcx"), sections.Keys.ToHashSet(), sections.Keys.ToDictionary(s => s, s => Tae(s)));
        }

        // 4. Sounds: every ported sound event (plus the weapon's sound offset) must reach media in the mod's bank.
        CheckSounds(r, modDir, portedSounds, soundOffsets);
        // 5. Effects: every ported effect ID an event spawns must be a .fxr in the mod's common effect binder.
        CheckEffects(r, modDir, portedEffects);
        return r;
    }

    private static void CheckBehaviour(Report r, BND4? behbnd, HashSet<int> sections, Dictionary<int, TAE?> taes)
    {
        if (sections.Count == 0)
            return;
        if (behbnd is null)
        {
            r.Error("weapons point at ported movesets but chr\\c0000.behbnd.dcx is missing (no clips: the moves can't play)");
            return;
        }
        var file = behbnd.Files.Where(f => f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)).MaxBy(f => f.Bytes.Length)!;
        hkRootLevelContainer root;
        try { root = (hkRootLevelContainer)new HavokBinarySerializer().Read(new MemoryStream(file.Bytes)); }
        catch (Exception e) { r.Error($"c0000.behbnd: behaviour does not read back: {e.GetType().Name}: {e.Message}"); return; }
        var clips = HavokGraph.All(root).OfType<hkbClipGenerator>().ToList();
        int maxId = clips.Max(c => c.m_animationInternalId);
        r.Info($"behaviour: {clips.Count} clips, highest internal animation ID {maxId} of 32767");
        if (maxId > 32000)
            r.Warn($"behaviour: internal animation IDs reach {maxId}, close to the 32767 limit");
        // One animation name, one internal ID: the game indexes clips by it.
        foreach (var g in clips.Where(c => c.m_animationName is not null).GroupBy(c => c.m_animationName!)
                     .Where(g => g.Select(c => c.m_animationInternalId).Distinct().Count() > 1).Take(10))
            r.Error($"behaviour: {g.Key} has {g.Select(c => c.m_animationInternalId).Distinct().Count()} different internal IDs");
        foreach (var g in clips.Where(c => c.m_animationName is not null).GroupBy(c => c.m_animationInternalId)
                     .Where(g => g.Select(c => c.m_animationName).Distinct().Count() > 1).Take(10))
            r.Error($"behaviour: internal ID {g.Key} is shared by {string.Join(", ", g.Select(c => c.m_animationName).Distinct().Take(3))}");
        var bySection = clips.Select(c => AnimName().Match(c.m_animationName ?? "")).Where(m => m.Success)
            .GroupBy(m => int.Parse(m.Groups[1].Value)).ToDictionary(g => g.Key, g => g.Select(m => int.Parse(m.Groups[2].Value)).ToHashSet());
        foreach (var section in sections.OrderBy(s => s))
        {
            if (!bySection.TryGetValue(section, out var have))
            {
                r.Error($"a{section}: no behaviour clips at all (the weapon would T-pose or keep its template's moves)");
                continue;
            }
            // Core attacks: R1 chain, R2 and 2H R1. A move in the TAE without a clip can't be reached.
            foreach (var core in new[] { 30000, 30010, 30500, 32000 })
                if (taes[section]?.Animations.Any(a => a.ID == core) == true && !have.Contains(core))
                    r.Error($"a{section:D3}_{core:D6}: in the TAE but no behaviour clip plays it");
        }
    }

    private static void CheckSounds(Report r, string modDir, List<(int Section, int Animation, int Kind, int Id)> sounds,
        Dictionary<int, int> offsets)
    {
        if (sounds.Count == 0)
            return;
        if (Directory.Exists(Path.Combine(modDir, SoundPort.DllSoundFolder)) && !File.Exists(Path.Combine(modDir, "sd", "enus", "cs_main.bnk")))
        {
            // Sounds method D: the DLL plays WAV files instead of the bank.
            var missing = sounds.Select(s => (s.Kind, s.Id)).Distinct()
                .Where(s => !File.Exists(Path.Combine(modDir, SoundPort.DllSoundFolder, $"{SoundPort.Letter(s.Kind)}{s.Id:D9}.wav"))).ToList();
            foreach (var (kind, id) in missing.Take(10))
                r.Error($"{SoundPort.Letter(kind)}{id:D9}: a TAE event plays it but {SoundPort.DllSoundFolder} has no WAV for it");
            return;
        }
        // Method E puts them in a bank of their own (sd\cs_c9990.bnk) and leaves cs_main alone.
        var bankName = File.Exists(Path.Combine(modDir, "sd", "enus", "cs_main.bnk")) ? Path.Combine("sd", "enus", "cs_main.bnk")
            : Path.Combine("sd", SoundPort.OwnBankName + ".bnk");
        var bankPath = Path.Combine(modDir, bankName);
        if (!File.Exists(bankPath))
        {
            r.Error($"{sounds.Count} events play ported sounds but neither sd\\enus\\cs_main.bnk nor sd\\{SoundPort.OwnBankName}.bnk is in the mod");
            return;
        }
        WwiseBank bank;
        try { bank = WwiseBank.Read(File.ReadAllBytes(bankPath)); }
        catch (Exception e) { r.Error($"{bankName} does not read back: {e.GetType().Name}: {e.Message}"); return; }
        // The game's own event index: every posted event should be listed under the bank that holds it.
        SoundIndex? soundIndex = null;
        var indexPath = Path.Combine(modDir, "sound", "soundbanksinfo.mobnkinfo");
        if (File.Exists(indexPath))
        {
            try { soundIndex = SoundIndex.Read(File.ReadAllBytes(indexPath)); }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException)
            {
                r.Error($"sound\\soundbanksinfo.mobnkinfo does not read back: {e.Message}");
            }
        }
        else
            r.Info("sounds: the game's sound index isn't edited (DARKSIGN_SOUND_INDEX=0, or the build couldn't read it)");
        int listed = 0;
        foreach (var dup in bank.Objects.GroupBy(o => o.Id).Where(g => g.Count() > 1).Take(5))
            r.Error($"{bankName}: HIRC ID {dup.Key} is used by {dup.Count()} objects (Wwise rejects the bank)");
        var index = new Dictionary<uint, int>();
        for (int i = 0; i < bank.Objects.Count; i++)
            index.TryAdd(bank.Objects[i].Id, i);
        int ok = 0;
        foreach (var s in sounds.Distinct())
        {
            var letter = SoundPort.Letter(s.Kind);
            if (letter is null)
                continue;
            // The game adds the weapon's wepSeIdOffset to the sound ID it posts.
            int posted = s.Id + offsets.GetValueOrDefault(s.Section);
            var name = $"Play_{letter}{posted:D9}";
            if (bank.Chain(FxSoundExplore.Fnv1(name)) is not { } chain)
            {
                r.Error($"a{s.Section:D3}_{s.Animation:D6}: plays {name}, which the mod's {bankName} doesn't have" +
                        (posted != s.Id ? $" (sound offset {posted - s.Id})" : ""));
                continue;
            }
            if (soundIndex is not null)
            {
                if (soundIndex.Lists(bank.BankId, FxSoundExplore.Fnv1(name)))
                    listed++;
                else
                    r.Error($"{name}: not listed under bank {bank.BankId} in sound\\soundbanksinfo.mobnkinfo (the game's own index of which sounds exist)");
            }
            var data = chain.Sound.Data;
            if (data.Length < 17) { r.Error($"{name}: Sound {chain.Sound.Id} is too short"); continue; }
            byte stream = data[8];
            uint media = BitConverter.ToUInt32(data, 9);
            var wem = Path.Combine(modDir, "sd", "wem", media.ToString()[..2], media + ".wem");
            if (stream == 0 && bank.Media(media) is null)
                r.Error($"{name}: Sound {chain.Sound.Id} embeds media {media}, which the bank doesn't hold");
            else if (stream != 0 && !File.Exists(wem) && bank.Media(media) is null)
                r.Error($"{name}: Sound {chain.Sound.Id} streams media {media}, but sd\\wem\\{media.ToString()[..2]}\\{media}.wem is missing");
            else if (HierarchyProblem(bank, index, chain.Sound) is { } problem)
                r.Error($"{name}: {problem} (Wwise drops a bank whose children don't come before their parents or aren't listed by them)");
            else
                ok++;
        }
        r.Info($"sounds: {ok} ported sound events reach their audio in {bankName}" + (soundIndex is null ? "" : $"; {listed} listed in the game's sound index"));
    }

    // Up a ported Sound's parents to the first actor-mixer: each parent must come after its child in the bank and
    // list it among its children (the community's rule for adding sounds; see WwiseBank.AddSound).
    private static string? HierarchyProblem(WwiseBank bank, Dictionary<uint, int> index, WwiseBank.HircObject sound)
    {
        var obj = sound;
        for (int depth = 0; depth < 8; depth++)
        {
            if (WwiseNode.Read(obj) is not { } node)
                return null; // not readable exactly: nothing to say
            uint parentId = node.Parent(obj.Data);
            if (parentId == 0 || bank.Get(parentId) is not { } parent || WwiseNode.Read(parent) is not { } parentNode)
                return null;
            if (index[parentId] < index[obj.Id])
                return $"node {obj.Id} comes after its parent {parentId}";
            if (parentNode.ChildrenAt >= 0 && !parentNode.Children.Contains(obj.Id))
                return $"node {obj.Id}'s parent {parentId} doesn't list it";
            if (parent.Type == WwiseBank.ActorMixerType)
                return null;
            obj = parent;
        }
        return null;
    }

    private static void CheckEffects(Report r, string modDir, List<(int Section, int Animation, int Id)> effects)
    {
        if (effects.Count == 0)
            return;
        var sfx = Path.Combine(modDir, "sfx");
        var have = new HashSet<int>();
        if (Directory.Exists(sfx))
            foreach (var file in Directory.EnumerateFiles(sfx, "*commoneffects*.ffxbnd.dcx"))
            {
                try
                {
                    foreach (var f in BND4.Read(DCX.Decompress(File.ReadAllBytes(file))).Files)
                        if (ItemText.Leaf(f.Name) is var leaf && leaf.EndsWith(".fxr", StringComparison.OrdinalIgnoreCase)
                            && int.TryParse(leaf[1..^4], out var id))
                            have.Add(id);
                }
                catch (Exception e) { r.Error($"{Path.GetFileName(file)} does not open: {e.GetType().Name}: {e.Message}"); }
            }
        int ok = 0;
        foreach (var e in effects.Distinct())
        {
            if (have.Contains(e.Id))
                ok++;
            else
                r.Error($"a{e.Section:D3}_{e.Animation:D6}: spawns effect {e.Id}, which isn't in the mod's common effect binder");
        }
        r.Info($"effects: {ok} ported effect references found");
    }

    private static void Points(Report r, ParamStore mod, PARAM.Row row, string field, string param)
    {
        int id = Field(row, field);
        if (id <= 0 || mod.Get(param) is not { } target)
            return;
        if (target[id] is null)
            r.Error($"weapon {row.ID}: {field} {id} has no {param} row");
    }

    private static int Field(PARAM.Row row, string field) =>
        row[field] is { } cell ? Convert.ToInt32(cell.Value) : -1;
}
