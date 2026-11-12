using SoulsFormats;

namespace DarkSign.Porter.Enemies;

/// <summary>
/// The template's own SpEffects on a ported enemy's NpcParam row (Devin, 2026-11-12: "all the enemies seem to be based
/// on skeletons, as when I killed them they respawned if I didn't hit them again"). A ported row is a copy of its Elden
/// Ring template's, so it kept the template's character effects: the Giant Beast Skeleton (c3061), template of 17 DS3
/// enemies including the hollow soldiers, carries 5832, whose state 143 brings a dead skeleton back unless it is hit
/// again. DS3's own effects are added next to them (<see cref="ChrEffects.ApplyNpc"/>).
///
/// For every fully ported enemy, the template effects that belong to the template are taken off the row first, the
/// same way for all of them: a revival (state 143) always, and any effect fewer than <see cref="MinCharacters"/> of Elden
/// Ring's characters carry (a Godskin's stance, a skeleton's revival, a pumpkin head's helmet). The shared ones (most
/// of Elden Ring's enemies carry 90030, 90060, 5400...: resistances and common reactions) stay. Work\reports\
/// enemy-template-effects.tsv lists what each template lost.
/// Fallbacks "enemy-effects": DARKSIGN_ENEMY_TEMPLATE_FX=keep keeps them all (builds before this option).
/// </summary>
public sealed class TemplateEffects
{
    public static bool Enabled => Environment.GetEnvironmentVariable("DARKSIGN_ENEMY_TEMPLATE_FX")?.Trim().ToLowerInvariant() != "keep";

    public const int MinCharacters = 8;
    public const int ReviveState = 143;
    public const int Slots = 32;
    public const string ReportFile = "enemy-template-effects.tsv";

    private readonly Dictionary<int, int> _characters = [];
    private readonly PARAM? _spEffects;
    private readonly Dictionary<int, (string Name, List<int> Dropped)> _byTemplate = [];

    /// <param name="npc">Elden Ring's NpcParam before any ported row is added (the character counts are vanilla's).</param>
    public TemplateEffects(PARAM npc, PARAM? spEffects)
    {
        _spEffects = spEffects;
        var seen = new Dictionary<int, HashSet<int>>();
        foreach (var row in npc.Rows)
            foreach (var id in Effects(row))
            {
                if (!seen.TryGetValue(id, out var chrs))
                    seen[id] = chrs = [];
                chrs.Add(row.ID / 10000);
            }
        foreach (var (id, chrs) in seen)
            _characters[id] = chrs.Count;
    }

    public static IEnumerable<int> Effects(PARAM.Row row) =>
        Enumerable.Range(0, Slots).Select(k => ParamDefaults.Int(row, $"spEffectID{k}")).Where(id => id > 0);

    /// <summary>How many of Elden Ring's characters (NpcParam ID / 10000) carry an effect.</summary>
    public int Characters(int effect) => _characters.GetValueOrDefault(effect);

    public bool Revives(int effect) =>
        _spEffects?[effect] is { } row && row.Cells.FirstOrDefault(c => c.Def.InternalName == "stateInfo") is { Value: { } v } && Convert.ToInt32(v) == ReviveState;

    /// <summary>Whether a template effect is the template's own (taken off a ported row).</summary>
    public bool TemplateOnly(int effect) => Revives(effect) || Characters(effect) < MinCharacters;

    /// <summary>Takes the template's own effects off a ported row; returns them.</summary>
    public List<int> Strip(PARAM.Row row, int templateNpc, string templateName)
    {
        var dropped = new List<int>();
        for (int k = 0; k < Slots; k++)
        {
            int id = ParamDefaults.Int(row, $"spEffectID{k}");
            if (id <= 0 || !TemplateOnly(id))
                continue;
            ParamDefaults.Set(row, $"spEffectID{k}", 0);
            dropped.Add(id);
        }
        // Close the gaps, so the effects that stay come first as in Elden Ring's rows.
        var kept = Effects(row).ToList();
        for (int k = 0; k < Slots; k++)
            ParamDefaults.Set(row, $"spEffectID{k}", k < kept.Count ? kept[k] : 0);
        if (!_byTemplate.ContainsKey(templateNpc))
            _byTemplate[templateNpc] = (templateName, dropped);
        return dropped;
    }

    public IEnumerable<string> ReportLines()
    {
        yield return $"# template NpcParam\tname\ttemplate effects taken off its ported rows (effect: Elden Ring characters with it, revival)\t(kept: effects {MinCharacters}+ characters carry)";
        foreach (var (npc, (name, dropped)) in _byTemplate.OrderBy(t => t.Key))
            yield return string.Join('\t', npc, name.Replace('\t', ' '),
                string.Join(" ", dropped.Select(id => $"{id}:{Characters(id)}{(Revives(id) ? " revival" : "")}")));
    }
}
