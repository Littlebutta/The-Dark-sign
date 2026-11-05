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
/// ShealthDamipolyId(2k) / (2k+1) sheathed from the right / left hand.
/// DARKSIGN_ABSORP=0 keeps every weapon on its template's row.
/// Fallbacks (launcher "two-hand" B and C): DARKSIGN_ABSORP=er copies the positions from an Elden Ring row that
/// already holds a second model in the left hand when two-handed (the game's own working layout); DARKSIGN_ABSORP=raw
/// copies DS3's dummy poly IDs as they are, without matching them through the bones.
/// </summary>
public static class AbsorpPort
{
    private static readonly string Mode = (Environment.GetEnvironmentVariable("DARKSIGN_ABSORP") ?? "").ToLowerInvariant();
    private static readonly bool Enabled = Mode != "0";

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
        int added = 0;
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
            for (int k = 0; k < Math.Min(models, 4); k++)
            {
                Set(row, $"right_{k}", Ds3(source, "OneHandDamipolyId", 2 * k), map, unmapped);
                Set(row, $"left_{k}", Ds3(source, "OneHandDamipolyId", 2 * k + 1), map, unmapped);
                Set(row, $"both_{k}", Ds3(source, "BothHandDamipolyId", k), map, unmapped);
                Set(row, $"leftBoth_{k}", Ds3(source, "BothHandDamipolyId", 4 + k), map, unmapped);
                Set(row, $"rightHang_{k}", Ds3(source, "ShealthDamipolyId", 2 * k), map, unmapped);
                Set(row, $"leftHang_{k}", Ds3(source, "ShealthDamipolyId", 2 * k + 1), map, unmapped);
                if (k == 0)
                    continue;
                // The template has no model k: it shows and places model k like model 0.
                foreach (var kind in new[] { "right", "left", "rightBoth", "leftBoth", "rightHang", "leftHang" })
                    if (row[$"dispPosType_{kind}_{k}"] is { } to && row[$"dispPosType_{kind}_0"] is { } from && Convert.ToInt32(to.Value) == 0)
                        to.Value = from.Value;
                if (row[$"wepInvisibleType_{k}"] is { } inv && Convert.ToInt32(inv.Value) == 0 && row["wepInvisibleType_0"] is { } inv0)
                    inv.Value = inv0.Value;
            }
            erAbsorp.Rows.Add(row);
            foreach (var weapon in erWeapons.Rows.Where(r => r.ID >= plan.NewId!.Value && r.ID < plan.NewId.Value + 10000))
                weapon["absorpParamId"].Value = Convert.ChangeType(row.ID, weapon["absorpParamId"].Value!.GetType());
            added++;
            if (plan.Ds3Id == 10180000 || added <= 3)
                notes.Add($"{plan.Name}: {models} models, row {row.ID} from DS3 {ds3AbsorpId} on template {templateAbsorpId}: " +
                          string.Join(" ", Enumerable.Range(0, Math.Min(models, 4)).Select(k =>
                              $"m{k} R{row[$"right_{k}"].Value} L{row[$"left_{k}"].Value} 2H{row[$"both_{k}"].Value} L2H{row[$"leftBoth_{k}"].Value}")) +
                          (unmapped.Count > 0 ? $"; DS3 dummies with no Elden Ring match: {string.Join(",", unmapped)}" : ""));
        }
        erAbsorp.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        Console.WriteLine($"absorp: {added} split weapons got DS3 model positions ({map.Count} DS3 dummy polys matched)");
        foreach (var note in notes.Take(12))
            Console.WriteLine("  " + note);
    }

    /// <summary>Elden Ring rows that place a model 1 in the left hand when two-handed, by how many weapons use them.</summary>
    private static List<PARAM.Row> ErSplitRows(PARAM absorp, PARAM weapons)
    {
        var uses = weapons.Rows.GroupBy(w => Convert.ToInt32(w["absorpParamId"].Value)).ToDictionary(g => g.Key, g => g.Count());
        return absorp.Rows.Where(r => r["leftBoth_1"] is { } c && Convert.ToInt32(c.Value) > 0
                                      && r["both_0"] is { } b && Convert.ToInt32(b.Value) > 0 && uses.ContainsKey(r.ID))
            .OrderByDescending(r => uses[r.ID]).ThenBy(r => r.ID).ToList();
    }

    /// <summary>DS3 dummy poly IDs unchanged (method 'raw'): every player dummy maps to itself.</summary>
    private static Dictionary<int, int> RawMap(GameArchives ds3) =>
        PlayerDummies(ds3).Keys.ToDictionary(id => id, id => id);

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

    /// <summary>DS3 player dummy poly ID -> Elden Ring's, matched through c0000's bones.</summary>
    public static Dictionary<int, int> DummyMap(GameArchives ds3, GameArchives er)
    {
        var a = PlayerDummies(ds3);
        var b = PlayerDummies(er);
        var map = new Dictionary<int, int>();
        foreach (var (id, (bone, pos)) in a)
        {
            if (b.TryGetValue(id, out var same) && string.Equals(same.Bone, bone, StringComparison.OrdinalIgnoreCase))
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
