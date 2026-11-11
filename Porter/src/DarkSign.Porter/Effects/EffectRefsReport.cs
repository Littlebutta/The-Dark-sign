using System.Text;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// effect-refs: a read-only report from the two games' params alone (no archives are opened, so it runs wherever the
/// regulation files are). For every DS3 weapon: the SpEffects, visual rows, bullets and effects (FXR) DS3's params tie
/// to it. Then a dry run of the visual and bullet ports into Elden Ring's params in memory (nothing is written), with
/// every new row printed next to its DS3 source, so the field mapping can be checked against the data.
/// Writes Work\reports\diag\effect-refs.txt. Usage: effect-refs [DS3 weapon id...]
/// </summary>
public static class EffectRefsReport
{
    public static int Run(PorterContext ctx, IReadOnlyList<string> args)
    {
        var only = args.Select(a => int.TryParse(a, out var v) ? v : -1).Where(v => v > 0).ToHashSet();
        var ds3 = ctx.LoadParams(PorterGame.DarkSouls3);
        var er = ctx.LoadParams(PorterGame.EldenRing);
        var r = new StringBuilder();
        void Log(string line) => r.AppendLine(line);
        var weapons = ds3.Get("EquipParamWeapon")!;
        var ds3SpEffects = ds3.Get("SpEffectParam")!;
        var ds3Vfx = ds3.Get("SpEffectVfxParam")!;
        var ds3Bullets = ds3.Get("Bullet")!;
        var refs = new Ds3EffectRefs(ds3);

        Log($"# DS3 weapon effect references ({DateTime.Now:yyyy-MM-dd HH:mm})");
        Log($"DS3: {weapons.Rows.Count} weapon rows, {ds3SpEffects.Rows.Count} SpEffects, {ds3Vfx.Rows.Count} visual rows, {ds3Bullets.Rows.Count} bullets");
        var all = refs.Fresh();
        int with = 0;
        foreach (var w in weapons.Rows.Where(w => w.ID > 0 && w.ID % 10000 == 0 && (only.Count == 0 || only.Contains(w.ID))))
        {
            var one = refs.Fresh();
            one.AddWeapon(w);
            all.AddWeapon(w);
            var model = string.Join(" ", Enumerable.Range(0, 8).Select(k => ParamDefaults.Int(w, $"weaponVfx{k}")));
            if (one.VisualFxr.Count + one.BulletFxr.Count == 0 && model.Replace("-1", "").Replace("0", "").Trim().Length == 0)
                continue;
            with++;
            Log($"\n## {w.ID} {w.Name}: variation {ParamDefaults.Int(w, "behaviorVariationId")}, weaponVfx0-7 {model}");
            Log($"  SpEffects {one.SpEffects.Count}: " + string.Join(", ", one.SpEffects.Order().Take(40).Select(id => $"{id} {ds3SpEffects[id]?.Name}".Trim())));
            Log($"  visual effects: {string.Join(" ", one.VisualFxr.Order())}");
            Log($"  bullets {one.Bullets.Count}: " + string.Join(", ", one.Bullets.Order().Take(40).Select(id => $"{id} {ds3Bullets[id]?.Name}".Trim())));
            Log($"  bullet effects: {string.Join(" ", one.BulletFxr.Order())}");
        }
        Log($"\n{with} DS3 weapons reference effects through their params; {all.SpEffects.Count} SpEffects, {all.VisualFxr.Count} visual effects, " +
            $"{all.Bullets.Count} bullets, {all.BulletFxr.Count} bullet effects in all");

        // SpEffect state (stateInfo) of effects with visuals, both games: which states draw a weapon enchant.
        foreach (var (game, store, fields) in new[]
                 {
                     ("DS3", ds3, Enumerable.Range(0, 8).Select(k => $"EffectVfx{k}").ToArray()),
                     ("ER", er, Enumerable.Range(0, 8).Select(k => k == 0 ? "vfxId" : $"vfxId{k}").ToArray()),
                 })
        {
            var sp = store.Get("SpEffectParam")!;
            var vfx = store.Get("SpEffectVfxParam")!;
            var byVfx = vfx.Rows.ToDictionary(v => v.ID);
            var enchantField = game == "DS3" ? "soulParamIdForWepEnchant1" : "soulParamIdForWepEnchant";
            Log($"\n# {game} SpEffects with visual rows, by stateInfo (count; effectType and enchant values of their rows; examples)");
            foreach (var g in sp.Rows.Where(s => fields.Any(f => ParamDefaults.Int(s, f) > 0)).GroupBy(s => ParamDefaults.Int(s, "stateInfo")).OrderByDescending(g => g.Count()).Take(40))
            {
                var rows = g.SelectMany(s => fields.Select(f => ParamDefaults.Int(s, f))).Where(byVfx.ContainsKey).Select(id => byVfx[id]).ToList();
                Log($"  stateInfo {g.Key}: {g.Count()}; effectType {Top(rows, "effectType")}; enchant {Top(rows, enchantField)}; e.g. " +
                    string.Join(" | ", g.Take(4).Select(s => $"{s.ID} {s.Name}".Trim())));
            }
        }

        // What enchant rows look like in each game (the blade span fields), to check the layout carries over.
        foreach (var (game, store) in new[] { ("DS3", ds3), ("ER", er) })
        {
            Log($"\n# {game} visual rows with effectType 1 or 2 (weapon enchants), first 12 distinct shapes");
            foreach (var g in store.Get("SpEffectVfxParam")!.Rows.Where(v => ParamDefaults.Int(v, "effectType") is 1 or 2)
                         .GroupBy(v => string.Join(" ", v.Cells.Where(c => c.Value is not null and not byte[] && !IsZero(c.Value) && Convert.ToDouble(c.Value) != -1
                                                                           && !c.Def.InternalName.Contains("SfxId", StringComparison.OrdinalIgnoreCase) && !c.Def.InternalName.Contains("SeId"))
                             .Select(c => $"{c.Def.InternalName}={c.Value}"))).OrderByDescending(g => g.Count()).Take(12))
                Log($"  {g.Count()}x (e.g. {g.First().ID} {g.First().Name}): {g.Key}");
        }

        // Dry run of the ports, with every effect counted as converted.
        var spPort = new SpEffectPort(er, ds3);
        var attackPort = new AttackParamPort(er, ds3) { Effects = spPort };
        var bulletPort = new BulletPort(er, ds3, spPort, attackPort.PortBulletAttack);
        var fake = new FxrPort.Result();
        foreach (var id in all.Fxr)
            fake.Effects[id] = FxrPort.IdBase + id;
        var erVfx = er.Get("SpEffectVfxParam")!;
        var erBullets = er.Get("Bullet")!;
        int vfxBefore = erVfx.Rows.Count, bulletsBefore = erBullets.Rows.Count;
        var portedEffects = all.SpEffects.Order().Select(id => (Ds3: id, Er: spPort.Port(id))).ToList();
        var portedBullets = all.Bullets.Order().Select(id => (Ds3: id, Er: bulletPort.Port(id))).ToList();
        spPort.BindVisuals(fake);
        bulletPort.Bind(fake);
        Log($"\n# Dry run: {portedEffects.Count(p => p.Er > 0)} of {portedEffects.Count} SpEffects ported ({spPort.Summary})");
        Log($"visual rows: {erVfx.Rows.Count - vfxBefore} new ({spPort.Visuals?.Summary}); bullets: {erBullets.Rows.Count - bulletsBefore} new ({bulletPort.Summary})");
        Log($"Elden Ring's most common values (the defaults new rows start from):");
        Log("  visual row: " + Describe(ParamDefaults.Common(erVfx), erVfx));
        Log("  bullet: " + Describe(ParamDefaults.Common(erBullets), erBullets));

        Log("\n## New visual rows (fields that differ from the default), under their DS3 source");
        var vfxDefaults = ParamDefaults.Common(erVfx);
        foreach (var (source, row) in ds3Vfx.Rows.Select(s => (s, Er: spPort.Visuals?.Ported.GetValueOrDefault(s.ID, -1) ?? -1)).Where(t => t.Er > 0).Select(t => (t.s, erVfx[t.Er]!)).Take(only.Count > 0 ? 400 : 60))
        {
            Log($"  DS3 {source.ID} {source.Name}: {NonZero(source)}");
            Log($"   -> ER {row.ID}: {Diff(row, vfxDefaults)}");
        }
        Log("\n## New bullets (fields that differ from the default), under their DS3 source");
        var bulletDefaults = ParamDefaults.Common(erBullets);
        foreach (var (d, e) in portedBullets.Where(p => p.Er > 0).Take(only.Count > 0 ? 400 : 60))
        {
            Log($"  DS3 {d} {ds3Bullets[d]!.Name}: {NonZero(ds3Bullets[d]!)}");
            Log($"   -> ER {e}: {Diff(erBullets[e]!, bulletDefaults)}");
            // The attack the bullet deals, as ported: where its power comes from decides whether it hurts at all.
            if (er.Get("AtkParam_Pc")?[ParamDefaults.Int(erBullets[e]!, "atkId_Bullet")] is { } atk)
                Log($"      attack {atk.ID}: " + string.Join(" ", new[] { "atkPhys", "atkMag", "atkFire", "atkThun", "atkDark", "atkPhysCorrection", "atkMagCorrection", "atkFireCorrection",
                    "atkThunCorrection", "atkDarkCorrection", "isAddBaseAtk", "spEffectId0", "spEffectId1", "spEffectId2" }.Select(f => $"{f}={ParamDefaults.Cell(atk, f)?.Value}")));
        }
        Log($"\n{attackPort.BulletBaseAttacks} bullet attacks take the weapon's attack power (percentages only in DS3)");

        Directory.CreateDirectory(ctx.WorkPath("reports", "diag"));
        var path = ctx.WorkPath("reports", "diag", "effect-refs.txt");
        File.WriteAllText(path, r.ToString());
        Console.WriteLine($"effect-refs: {with} DS3 weapons with effects in their params; dry run {erVfx.Rows.Count - vfxBefore} visual rows, " +
                          $"{erBullets.Rows.Count - bulletsBefore} bullets -> {path} ({r.Length / 1024} KB)");
        return 0;
    }

    private static string Top(List<PARAM.Row> rows, string field) =>
        string.Join(" ", rows.GroupBy(v => ParamDefaults.Int(v, field)).OrderByDescending(g => g.Count()).Take(5).Select(g => $"{g.Key}x{g.Count()}"));

    private static string Describe(object?[] values, PARAM param) =>
        string.Join(" ", param.AppliedParamdef.Fields.Select((f, i) => (f.InternalName, Value: values[i])).Where(t => t.Value is not null && !IsZero(t.Value))
            .Select(t => $"{t.InternalName}={t.Value}"));

    private static string NonZero(PARAM.Row row) =>
        string.Join(" ", row.Cells.Where(c => c.Value is not null and not byte[] && !IsZero(c.Value) && Convert.ToDouble(c.Value) != -1).Select(c => $"{c.Def.InternalName}={c.Value}"));

    private static string Diff(PARAM.Row row, object?[] defaults) =>
        string.Join(" ", row.Cells.Select((c, i) => (c, i)).Where(t => t.c.Value is not null and not byte[] && !Equals(t.c.Value, defaults[t.i]))
            .Select(t => $"{t.c.Def.InternalName}={t.c.Value}"));

    private static bool IsZero(object value) => value is not byte[] && Convert.ToDouble(value) == 0;
}
