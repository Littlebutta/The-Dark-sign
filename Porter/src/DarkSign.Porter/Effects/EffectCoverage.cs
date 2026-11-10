using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// The per-weapon effect report (Work\reports\weapon-fx.tsv): for every ported DS3 weapon, the DS3 effects (FXR) it
/// shows by where they come from, how many of each were converted, and why the rest were left out. The sources are
/// the class moveset's effect events (shared by every weapon of the class), the weapon's own art section's effect
/// events, the effects it plays on its own model while held, what its SpEffects look like (worn, on hit, cast by a move, a behaviour or a bullet), and its bullets.
/// A weapon "has effects of its own" when anything but the class moves shows one.
/// </summary>
public static class EffectCoverage
{
    public static void Write(PorterContext ctx, IReadOnlyList<WeaponPlan> ports, PARAM ds3Weapons, Func<int, TAE?> ds3Tae,
        EffectEvents effectEvents, Ds3EffectRefs refs, FxrPort.Result effects, bool visuals, bool bullets)
    {
        var lines = new List<string>
        {
            "weapon\tDS3 ID\tclass move effects (shown/all)\tart move effects\ton the weapon model\tSpEffect visuals\tbullet effects\tDS3 SpEffects\tDS3 bullets\tleft out (effect: why)",
        };
        var sectionFx = new Dictionary<int, HashSet<int>>();
        var sectionSpEffects = new Dictionary<int, List<int>>();
        void Load(int section)
        {
            if (sectionFx.ContainsKey(section))
                return;
            var events = section > 0 && ds3Tae(section) is { } tae ? tae.Animations.SelectMany(a => a.Events).ToList() : [];
            sectionFx[section] = effectEvents.Ids(events).ToHashSet();
            sectionSpEffects[section] = MovesetPort.TaeSpEffects(events).Distinct().ToList();
        }
        int own = 0, all = 0, partly = 0, none = 0;
        var leftOut = new Dictionary<string, HashSet<int>>();
        var leftOutWeapons = new Dictionary<string, List<string>>();
        foreach (var plan in ports.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var weapon = ds3Weapons[plan.Ds3Id]!;
            int cls = ParamDefaults.Int(weapon, "wepmotionCategory"), art = ParamDefaults.Int(weapon, "spAtkCategory");
            Load(cls);
            Load(art);
            var r = refs.Fresh();
            r.AddWeapon(weapon);
            foreach (var id in sectionSpEffects[cls].Concat(art == cls ? [] : sectionSpEffects[art]))
                r.AddSpEffect(id);
            var classFx = sectionFx[cls];
            var artFx = art == cls ? [] : sectionFx[art].Except(classFx).ToHashSet();
            var visualFx = visuals ? r.VisualFxr : [];
            var bulletFx = bullets ? r.BulletFxr : [];
            var residentFx = visuals ? r.ResidentFxr : [];
            string Share(IReadOnlyCollection<int> ids) => ids.Count == 0 ? "" : $"{ids.Count(id => effects.Map(id) is not null)}/{ids.Count}";
            var ownFx = artFx.Concat(r.VisualFxr).Concat(r.BulletFxr).Concat(r.ResidentFxr).Distinct().ToList();
            var missing = classFx.Concat(artFx).Concat(visualFx).Concat(bulletFx).Concat(residentFx).Distinct().Where(id => effects.Map(id) is null).Order().ToList();
            var ownMissing = ownFx.Where(id => effects.Map(id) is null).ToList();
            if (ownFx.Count > 0)
            {
                own++;
                if (ownMissing.Count == 0) all++;
                else if (ownMissing.Count < ownFx.Count) partly++;
                else none++;
            }
            foreach (var id in ownMissing)
            {
                var why = Why(effects, id, visuals, bullets, r);
                (leftOut.TryGetValue(why, out var ids) ? ids : leftOut[why] = []).Add(id);
                var names = leftOutWeapons.TryGetValue(why, out var list) ? list : leftOutWeapons[why] = [];
                if (!names.Contains(plan.Name))
                    names.Add(plan.Name);
            }
            lines.Add(string.Join('\t', plan.Name, plan.Ds3Id, Share(classFx), Share(artFx), Share(r.ResidentFxr), Share(r.VisualFxr), Share(r.BulletFxr),
                r.SpEffects.Count, r.Bullets.Count, string.Join("; ", missing.Select(id => $"f{id}: {Why(effects, id, visuals, bullets, r)}"))));
        }
        var path = ctx.WorkPath("reports", "weapon-fx.tsv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
        Console.WriteLine($"effect coverage: {ports.Count} weapons, {own} show DS3 effects of their own (art moves, on the model, SpEffect visuals, bullets): " +
                          $"{all} all converted, {partly} partly, {none} none; per weapon: {path}");
        foreach (var (why, ids) in leftOut.OrderByDescending(l => l.Value.Count))
            Console.WriteLine($"effect coverage: left out ({why}): {ids.Count} effects on {leftOutWeapons[why].Count} weapons, e.g. " +
                              string.Join(", ", leftOutWeapons[why].Take(8)));
    }

    private static string Why(FxrPort.Result effects, int id, bool visuals, bool bullets, Ds3EffectRefs r)
    {
        if (effects.Reasons.TryGetValue(id, out var reason))
            return reason;
        if (!visuals && (r.VisualFxr.Contains(id) || r.ResidentFxr.Contains(id)))
            return "SpEffect visuals off";
        if (!bullets && r.BulletFxr.Contains(id))
            return "bullets off";
        return "not converted";
    }
}
