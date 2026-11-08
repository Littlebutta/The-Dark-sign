using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// Where a weapon's models sit on the body: WepAbsorpPosParam ("absorb position"), one dummy poly of the player model
/// per model (0-3) and per grip (right hand, left hand, two-handed, sheathed). Split weapons keep several models in one
/// part (Friede's Great Scythe, twinblades, paired swords); DS3 moves model 1 to the left hand when two-handing, which
/// is how the scythe splits in two. Ported weapons start with their Elden Ring template's row, which only places
/// model 0, so a weapon whose DS3 part has more than one model gets a new row: the template's, with each model's
/// positions taken from DS3. DS3 dummy poly IDs are translated to Elden Ring's through the two player models: same ID
/// on the same bone, else the Elden Ring dummy on that bone nearest to it.
/// DS3 field layout per model k (Paramdex names): OneHandDamipolyId(2k) right hand, (2k+1) left hand,
/// BothHandDamipolyId(k) two-handed in the right hand, BothHandDamipolyId(4+k) two-handed in the left hand,
/// ShealthDamipolyId(2k) / (2k+1) = Elden Ring leftHang_k / rightHang_k (the struct is the same field for field). DS3 Unk22-25 =
/// wepInvisibleType_0-3 and Unk30-53 = the dispPosType bytes (6 per model), copied so model 1 shows only where DS3 shows it.
/// DARKSIGN_ABSORP=0 keeps every weapon on its template's row.
/// Fallbacks (launcher "two-hand" B and C): DARKSIGN_ABSORP=er copies the positions from an Elden Ring row that
/// already holds a second model in the left hand when two-handed (the game's own working layout); DARKSIGN_ABSORP=raw
/// copies DS3's dummy poly IDs as they are, without matching them through the bones.
/// </summary>
public static class AbsorpPort
{
    private static readonly string Mode = (Environment.GetEnvironmentVariable("DARKSIGN_ABSORP") ?? "").ToLowerInvariant();
    private static readonly bool Enabled = Mode != "0";
    // Elden Ring c0000's left-weapon dummy (L_Weapon, as in DS3), where paired rows hold model 1 two-handed.
    private const int LeftWeaponDummy = 21;
    // DARKSIGN_FRIEDE_OVERRIDE=keep: no left-hand slot for the art's second blade (as DS3's row has it).
    private static readonly bool FriedeLeftHand = Environment.GetEnvironmentVariable("DARKSIGN_FRIEDE_OVERRIDE") != "keep";
    // DARKSIGN_PAIRED_GRIP=0: paired weapons keep the template's isDualBlade and hold motions (positions still DS3's).
    private static readonly bool PairedGrip = Environment.GetEnvironmentVariable("DARKSIGN_PAIRED_GRIP") != "0";

    public static void Apply(GameArchives erArchives, GameArchives ds3Archives, ParamStore er, ParamStore ds3, IReadOnlyList<WeaponPlan> plans)
    {
        if (!Enabled)
        {
            Console.WriteLine("absorp: off (DARKSIGN_ABSORP=0), weapons keep their template's model positions");
            return;
        }
        var erWeapons = er.Get("EquipParamWeapon")!;
        var ds3Weapons = ds3.Get("EquipParamWeapon")!;
        var erAbsorp = er.Get("WepAbsorpPosParam");
        var ds3Absorp = ds3.Get("WepAbsorpPosParam") ?? ds3.Get("WEP_ABSORP_POS_PARAM_ST");
        if (erAbsorp is null || ds3Absorp is null)
        {
            Console.WriteLine("absorp: WepAbsorpPosParam missing in one of the games, skipped");
            return;
        }
        var map = Mode == "raw" ? RawMap(ds3Archives) : DummyMap(ds3Archives, erArchives);
        // DARKSIGN_ABSORP=er: Elden Ring rows whose model 1 sits in the left hand when two-handed, most used first.
        var erSplitRows = Mode == "er" ? ErSplitRows(erAbsorp, erWeapons) : [];
        if (Mode == "er")
            Console.WriteLine($"absorp: method 'er': {erSplitRows.Count} Elden Ring rows hold model 1 in the left hand two-handed" +
                              (erSplitRows.Count > 0 ? $" (using row {erSplitRows[0].ID})" : "; falling back to DS3 positions"));
        int nextId = Math.Max(erAbsorp.Rows.Max(r => r.ID) + 1, 900_000);
        int added = 0, pairedCount = 0, friedeCount = 0;
        var ds3Bones = PlayerDummies(ds3Archives).ToDictionary(d => d.Key, d => d.Value.Bone);
        // Hold motions Elden Ring's own weapons use; a DS3 value Elden Ring never uses stays the template's.
        var erGrips = erWeapons.Rows.SelectMany(r => new[] { r["wepmotionOneHandId"], r["wepmotionBothHandId"] })
            .Where(c => c is not null).Select(c => Convert.ToInt32(c!.Value)).ToHashSet();
        var notes = new List<string>();
        foreach (var plan in plans.Where(p => p.Status == "port"))
        {
            var ds3Row = ds3Weapons[plan.Ds3Id]!;
            int models = ModelCount(ds3Archives, Convert.ToInt32(ds3Row["equipModelId"].Value));
            if (models < 2)
                continue;
            int ds3AbsorpId = Convert.ToInt32(ds3Row["wepAbsorpPosId"].Value);
            int templateAbsorpId = Convert.ToInt32(erWeapons[plan.TemplateId!.Value]!["absorpParamId"].Value);
            if (ds3Absorp[ds3AbsorpId] is not { } source || erAbsorp[templateAbsorpId] is not { } template)
            {
                notes.Add($"{plan.Name}: no absorb row (DS3 {ds3AbsorpId}, template {templateAbsorpId})");
                continue;
            }
            var row = new PARAM.Row(nextId++, "", erAbsorp.AppliedParamdef);
            var unmapped = new SortedSet<int>();
            if (erSplitRows.Count > 0)
            {
                // The Elden Ring split row as a whole (positions, display types and visibility for every model).
                var split = erSplitRows[0];
                for (int i = 0; i < row.Cells.Count; i++)
                    row.Cells[i].Value = split.Cells[i].Value;
                models = 0; // nothing more to place
            }
            else
                for (int i = 0; i < row.Cells.Count; i++)
                    row.Cells[i].Value = template.Cells[i].Value;
            // DS3's display bytes are taken for weapons whose model 1 only shows while in use (wepInvisibleType_1 = 2:
            // Friede's scythe, paired swords, twinblades, fists); weapons with a plain sheath part keep the behaviour
            // that already worked (template bytes).
            bool ds3Display = HasDs3Display(source) && Convert.ToInt32(source["Unk23"]!.Value) == 2;
            for (int k = 0; k < Math.Min(models, 4); k++)
            {
                // Same struct in both games, field for field: Elden Ring's per-model block (right, left, both,
                // leftHang, rightHang) is DS3's (OneHand 2k, OneHand 2k+1, BothHand k, Shealth 2k, Shealth 2k+1).
                Set(row, $"right_{k}", Ds3(source, "OneHandDamipolyId", 2 * k), map, unmapped);
                Set(row, $"left_{k}", Ds3(source, "OneHandDamipolyId", 2 * k + 1), map, unmapped);
                Set(row, $"both_{k}", Ds3(source, "BothHandDamipolyId", k), map, unmapped);
                Set(row, $"leftBoth_{k}", Ds3(source, "BothHandDamipolyId", 4 + k), map, unmapped);
                Set(row, $"leftHang_{k}", Ds3(source, "ShealthDamipolyId", 2 * k), map, unmapped);
                Set(row, $"rightHang_{k}", Ds3(source, "ShealthDamipolyId", 2 * k + 1), map, unmapped);
                if (ds3Display)
                {
                    // DS3's unnamed bytes Unk22-25 are wepInvisibleType_0-3 and Unk30-53 the 24 dispPosType bytes:
                    // they decide whether model k shows, and how, in each grip. Model 1 of a split weapon shows only
                    // when two-handed; the template's (or model 0's) values showed it in the right hand.
                    CopyByte(source, $"Unk{22 + k}", row, $"wepInvisibleType_{k}");
                    for (int j = 0; j < DispKinds.Length; j++)
                        CopyByte(source, $"Unk{30 + 6 * k + j}", row, $"dispPosType_{DispKinds[j]}_{k}");
                    continue;
                }
                if (k == 0)
                    continue;
                // The template has no model k: it shows and places model k like model 0.
                foreach (var kind in DispKinds)
                    if (row[$"dispPosType_{kind}_{k}"] is { } to && row[$"dispPosType_{kind}_0"] is { } from && Convert.ToInt32(to.Value) == 0)
                        to.Value = from.Value;
                if (row[$"wepInvisibleType_{k}"] is { } inv && Convert.ToInt32(inv.Value) == 0 && row["wepInvisibleType_0"] is { } inv0)
                    inv.Value = inv0.Value;
            }
            erAbsorp.Rows.Add(row);
            // A paired weapon (DS3 shows model 1 in the left hand when two-handed: twinblades, Ringed Knight Paired
            // Greatswords, Valorheart, fists) also needs what Elden Ring's own paired weapons have: isDualBlade = 1,
            // and DS3's hold motions (wepmotionOneHandId / wepmotionBothHandId: the grip pose; RKPG and Elden Ring's
            // Starscourge Greatsword both use 2). With the template's two-handed grip the hands meet on one hilt,
            // which pulled the left-hand blade into the right hand.
            bool paired = IsPaired(source, ds3Bones);
            // Friede's scythe: DS3's row keeps model 1 on the back in every grip (35/36) because DS3's engine moves it by
            // weapon ID ("Friede's Scythe Override": two-handed, and in the art through TAE 712 ChangeType 5). Elden Ring
            // has no such hardcode, so the 1.0.183 test swung Friede's two-scythe animations with the second scythe
            // still on the back. It is laid out like the Ringed Knight Paired Greatswords instead (which hold correctly):
            // two-handed and "1H left weapon" (where MovesetPort points the art's override) at the left-weapon dummy,
            // shown there, and the weapon is marked paired (isDualBlade) below.
            if (!paired && ds3Display && FriedeLeftHand && models >= 2)
            {
                for (int k = 1; k < Math.Min(models, 4); k++)
                {
                    foreach (var field in new[] { $"left_{k}", $"both_{k}", $"leftBoth_{k}" })
                        if (row[field] is { } cell)
                            cell.Value = Convert.ChangeType(LeftWeaponDummy, cell.Value!.GetType());
                    foreach (var field in new[] { $"dispPosType_rightBoth_{k}", $"dispPosType_leftBoth_{k}" })
                        if (row[field] is { } cell)
                            cell.Value = Convert.ChangeType(1, cell.Value!.GetType());
                }
                paired = true;
                friedeCount++;
            }
            // Every Elden Ring paired row marks model 0 in the left hand as 1 (DS3 wrote 0 there).
            if (paired && erSplitRows.Count == 0 && row["dispPosType_left_0"] is { } left0)
                left0.Value = Convert.ChangeType(1, left0.Value!.GetType());
            var gripNote = "";
            foreach (var weapon in erWeapons.Rows.Where(r => r.ID >= plan.NewId!.Value && r.ID < plan.NewId.Value + 10000))
            {
                weapon["absorpParamId"].Value = Convert.ChangeType(row.ID, weapon["absorpParamId"].Value!.GetType());
                if (!paired || !PairedGrip)
                    continue;
                if (weapon["isDualBlade"] is { } dual)
                    dual.Value = Convert.ChangeType(1, dual.Value!.GetType());
                foreach (var field in new[] { "wepmotionOneHandId", "wepmotionBothHandId" })
                    if (weapon[field] is { } cell && ds3Row[field] is { } from && erGrips.Contains(Convert.ToInt32(from.Value)))
                        cell.Value = Convert.ChangeType(Convert.ToInt32(from.Value), cell.Value!.GetType());
                gripNote = $" paired: isDualBlade 1, grip {weapon["wepmotionOneHandId"]?.Value}/{weapon["wepmotionBothHandId"]?.Value}";
            }
            if (paired)
                pairedCount++;
            added++;
            if (plan.Ds3Id is 10180000 or 16160000 or 16000000 || added <= 3)
                notes.Add($"{plan.Name}: {models} models, row {row.ID} from DS3 {ds3AbsorpId} on template {templateAbsorpId}: " +
                          string.Join(" ", Enumerable.Range(0, Math.Min(models, 4)).Select(k =>
                              $"m{k} R{row[$"right_{k}"].Value} L{row[$"left_{k}"].Value} 2H{row[$"both_{k}"].Value} L2H{row[$"leftBoth_{k}"].Value}" +
                              $" show{row[$"wepInvisibleType_{k}"].Value} disp2H{row[$"dispPosType_rightBoth_{k}"].Value}")) +
                          gripNote + (unmapped.Count > 0 ? $"; DS3 dummies with no Elden Ring match: {string.Join(",", unmapped)}" : ""));
        }
        erAbsorp.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        Console.WriteLine($"absorp: {added} split weapons got DS3 model positions ({map.Count} DS3 dummy polys matched), " +
                          $"{pairedCount} paired (left blade in the left hand two-handed; {friedeCount} of them Friede-style, moved by DS3's engine)" + (PairedGrip ? "" : " [grip/isDualBlade off: DARKSIGN_PAIRED_GRIP=0]"));
        foreach (var note in notes.Take(12))
            Console.WriteLine("  " + note);
    }

    /// <summary>Elden Ring rows that place a model 1 in the left hand when two-handed, by how many weapons use them.</summary>
    private static List<PARAM.Row> ErSplitRows(PARAM absorp, PARAM weapons)
    {
        var uses = weapons.Rows.GroupBy(w => Convert.ToInt32(w["absorpParamId"].Value)).ToDictionary(g => g.Key, g => g.Count());
        // Elden Ring's paired rows (claws 22, fists 42, Falx 101, Starscourge 260, Rellana's 261...) all hide model 1
        // except when it is in use (wepInvisibleType_1 = 2) and hold it in the left hand two-handed (both_1 = 21 or
        // 2117, on the left hand); ordinary rows keep model 1 as a sheath part (wepInvisibleType_1 = 1).
        return absorp.Rows.Where(r => r["wepInvisibleType_1"] is { } inv && Convert.ToInt32(inv.Value) == 2
                                      && r["both_1"] is { } c && Convert.ToInt32(c.Value) > 0
                                      && r["both_0"] is { } b && Convert.ToInt32(b.Value) > 0 && uses.ContainsKey(r.ID))
            .OrderByDescending(r => uses[r.ID]).ThenBy(r => r.ID).ToList();
    }

    /// <summary>DS3 dummy poly IDs unchanged (method 'raw'): every player dummy maps to itself.</summary>
    private static Dictionary<int, int> RawMap(GameArchives ds3) =>
        PlayerDummies(ds3).Keys.ToDictionary(id => id, id => id);

    /// <summary>
    /// Whether DS3 holds model 1 in the left hand when the weapon is two-handed: BothHandDamipolyId1 is a dummy on the
    /// left hand or left weapon bone (DS3 21 / 23 / 27 on L_Weapon). Friede's scythe keeps model 1 on the back (35).
    /// </summary>
    private static bool IsPaired(PARAM.Row ds3Absorp, Dictionary<int, string> ds3Bones) =>
        Ds3(ds3Absorp, "BothHandDamipolyId", 1) is var d && d > 0 && ds3Bones.TryGetValue(d, out var bone)
        && (bone.Equals("L_Weapon", StringComparison.OrdinalIgnoreCase) || bone.Equals("L_Hand", StringComparison.OrdinalIgnoreCase));

    // Elden Ring's dispPosType bytes per model, in struct order (DS3 Unk30 + 6k + j).
    private static readonly string[] DispKinds = ["right", "left", "rightBoth", "leftBoth", "rightHang", "leftHang"];

    /// <summary>Whether the DS3 def names the display bytes (Paramdex: Unk22-25, Unk30-53).</summary>
    private static bool HasDs3Display(PARAM.Row row) => row["Unk22"] is not null && row["Unk53"] is not null;

    private static void CopyByte(PARAM.Row from, string fromField, PARAM.Row to, string toField)
    {
        if (from[fromField] is { } a && to[toField] is { } b)
            b.Value = Convert.ChangeType(Convert.ToInt32(a.Value), b.Value!.GetType());
    }

    private static int Ds3(PARAM.Row row, string field, int index) =>
        row[$"{field}{index}"] is { } cell ? Convert.ToInt32(cell.Value) : -1;

    private static void Set(PARAM.Row row, string field, int ds3Dummy, Dictionary<int, int> map, SortedSet<int> unmapped)
    {
        if (ds3Dummy <= 0 || row[field] is not { } cell)
            return;
        if (!map.TryGetValue(ds3Dummy, out var erDummy))
        {
            unmapped.Add(ds3Dummy);
            return;
        }
        cell.Value = Convert.ChangeType(erDummy, cell.Value!.GetType());
    }

    /// <summary>How many models (FLVERs, not counting low-detail parts) a DS3 weapon part holds.</summary>
    public static int ModelCount(GameArchives ds3, int model)
    {
        var path = ModelPort.PartsPath(model);
        if (!ds3.Contains(path))
            return 0;
        try { return BND4.Read(ds3.Read(path)).Files.Count(f => FLVER2.Is(f.Bytes)); }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException) { return 0; }
    }

    // Metres: how far apart the same dummy ID may sit in the two player models and still count as the same place.
    private const float SameSpot = 0.25f;

    /// <summary>DS3 player dummy poly ID -> Elden Ring's, matched through c0000's bones.</summary>
    public static Dictionary<int, int> DummyMap(GameArchives ds3, GameArchives er)
    {
        var a = PlayerDummies(ds3);
        var b = PlayerDummies(er);
        var map = new Dictionary<int, int>();
        foreach (var (id, (bone, pos)) in a)
        {
            // Same ID on the same bone, or at nearly the same spot on the body: Elden Ring re-parented DS3's storage
            // dummies (back 33-40 moved from Spine2 to Spine1), so the bone alone sent Friede's back blade (DS3 35/36)
            // to an unrelated Spine2 dummy (266) that no grip shows.
            if (b.TryGetValue(id, out var same) && (string.Equals(same.Bone, bone, StringComparison.OrdinalIgnoreCase)
                                                    || Vector3.Distance(same.Pos, pos) <= SameSpot))
            {
                map[id] = id;
                continue;
            }
            var nearest = b.Where(d => string.Equals(d.Value.Bone, bone, StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => Vector3.Distance(d.Value.Pos, pos)).Select(d => (int?)d.Key).FirstOrDefault();
            if (nearest is { } n)
                map[id] = n;
        }
        return map;
    }

    private static Dictionary<int, (string Bone, Vector3 Pos)> PlayerDummies(GameArchives game)
    {
        var result = new Dictionary<int, (string, Vector3)>();
        const string path = "/chr/c0000.chrbnd.dcx";
        if (!game.Contains(path))
            return result;
        var file = BND4.Read(game.Read(path)).Files.FirstOrDefault(f => FLVER2.Is(f.Bytes));
        if (file is null)
            return result;
        var flver = FLVER2.Read(file.Bytes);
        foreach (var d in flver.Dummies)
        {
            int boneIndex = d.AttachBoneIndex >= 0 ? d.AttachBoneIndex : d.ParentBoneIndex;
            if (boneIndex < 0 || boneIndex >= flver.Nodes.Count)
                continue;
            result.TryAdd(d.ReferenceID, (flver.Nodes[boneIndex].Name, d.Position));
        }
        return result;
    }
}
