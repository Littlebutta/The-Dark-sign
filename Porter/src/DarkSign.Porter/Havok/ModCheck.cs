using System.Text;
using System.Text.RegularExpressions;
using HKLib.hk2018;
using HKLib.Serialization.hk2018.Binary;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// mod-check &lt;weapon row ID&gt;: read-only look at what the BUILT mod (Mods\ER) gives one weapon, to find why its moveset
/// doesn't play: its regulation row, its section's TAE (IDs, mini headers, events of the first moves), the section's
/// animations in the anim binder, and the behaviour clips and selectors that reach the section. Writes
/// Work\reports\explore\mod-check-&lt;id&gt;.txt.
/// </summary>
public static class ModCheck
{
    public static int Run(PorterContext ctx, int weaponId)
    {
        var report = new StringBuilder();
        void Log(string line) { Console.WriteLine(line); report.AppendLine(line); }

        // 1. The mod's regulation row.
        var mod = ParamStore.Load(PorterGame.EldenRing, Path.GetDirectoryName(ctx.ModPath("regulation.bin"))!, ctx.Data);
        var weapons = mod.Get("EquipParamWeapon")!;
        var row = weapons[weaponId];
        if (row is null)
        {
            Log($"Mods\\ER\\regulation.bin has no EquipParamWeapon {weaponId}");
            return 1;
        }
        var fields = new[] { "wepType", "wepmotionCategory", "spAtkcategory", "behaviorVariationId", "weaponCategory", "equipModelId", "residentSpEffectId" };
        Log($"row {weaponId} '{row.Name}': " + string.Join(", ", fields.Where(f => row[f] is not null).Select(f => $"{f} {row[f].Value}")));
        int section = Convert.ToInt32(row["wepmotionCategory"].Value);

        // 2. The section's TAE in the mod's c0000.anibnd.
        TAE.ValidateEventBank = false;
        var main = BND4.Read(DCX.Decompress(File.ReadAllBytes(ctx.ModPath("chr", "c0000.anibnd.dcx"))));
        var taeFile = main.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).Equals($"a{section:D3}.tae", StringComparison.OrdinalIgnoreCase));
        var neighbours = main.Files.Where(f => f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase)).Select(f => $"{ItemText.Leaf(f.Name)}#{f.ID}").ToList();
        Log($"c0000.anibnd: {main.Files.Count} files; TAEs {string.Join(" ", neighbours.Take(6))} ... {string.Join(" ", neighbours.TakeLast(6))}");
        if (taeFile is null)
            Log($"  NO a{section:D3}.tae");
        else
        {
            var tae = TAE.Read(taeFile.Bytes);
            Log($"  a{section:D3}.tae id {taeFile.ID}, TAE id {tae.ID}, {tae.Animations.Count} animations, format {tae.Format}");
            foreach (var anim in tae.Animations.Where(a => a.ID is 30000 or 30010 or 30020 or 30500 or 32000 or 32010).Take(6))
            {
                var head = anim.MiniHeader switch
                {
                    TAE.Animation.AnimMiniHeader.ImportOtherAnim i => $"import {i.ImportFromAnimID} (unk {i.Unknown})",
                    TAE.Animation.AnimMiniHeader.Standard s => $"standard (loop {s.IsLoopByDefault}, import hkx {s.ImportsHKX}, from {s.ImportHKXSourceAnimID})",
                    var h => h?.GetType().Name ?? "none",
                };
                Log($"  anim {anim.ID}: {head}, file '{anim.AnimFileName}', {anim.Events.Count} events");
                // Every event, sounds and effects included, with its event group (ungrouped events don't play).
                foreach (var e in anim.Events.OrderBy(e => e.StartTime))
                    Log($"    type {e.Type} {e.StartTime:0.###}-{e.EndTime:0.###} {Convert.ToHexString(e.GetParameterBytes(false))}" +
                        (e.Group is null ? " NO GROUP" : $" group {e.Group.GroupType}"));
            }
        }

        // 3. The section's animations in the anim binder.
        var anims = BND4.Read(DCX.Decompress(File.ReadAllBytes(ctx.ModPath("chr", "c0000_a9x.anibnd.dcx"))));
        var prefix = $"a{section:D3}_";
        var own = anims.Files.Where(f => ItemText.Leaf(f.Name).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        Log($"c0000_a9x: {anims.Files.Count} files, {own.Count} named {prefix}*, e.g. {string.Join(", ", own.Take(5).Select(f => $"{ItemText.Leaf(f.Name)}#{f.ID} {f.Bytes.Length}b"))}; names like '{own.FirstOrDefault()?.Name}'");
        var sample = anims.Files.FirstOrDefault(f => !own.Contains(f) && f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase));
        Log($"  a vanilla one for comparison: '{sample?.Name}' #{sample?.ID} flags {sample?.Flags}; ours flags {own.FirstOrDefault()?.Flags}");

        // 4. Behaviour clips and selectors reaching the section.
        var behbnd = BND4.Read(DCX.Decompress(File.ReadAllBytes(ctx.ModPath("chr", "c0000.behbnd.dcx"))));
        var behaviorFile = behbnd.Files.Where(f => f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)).MaxBy(f => f.Bytes.Length)!;
        var root = (hkRootLevelContainer)new HavokBinarySerializer().Read(new MemoryStream(behaviorFile.Bytes));
        var graph = HavokGraph.All(root);
        var clips = graph.OfType<hkbClipGenerator>().ToList();
        var mine = clips.Where(c => (c.m_animationName ?? "").StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        Log($"behaviour: {clips.Count} clips, {mine.Count} for {prefix}*; internal ids {mine.Select(c => (int)c.m_animationInternalId).DefaultIfEmpty().Min()}..{mine.Select(c => (int)c.m_animationInternalId).DefaultIfEmpty().Max()}");
        var selectors = graph.OfType<CustomManualSelectorGenerator>().ToList();
        foreach (var s in selectors.Where(s => s.m_generators.OfType<hkbClipGenerator>().Any(c => mine.Contains(c)) && s.m_animId is 30000 or 30010 or 30020 or 30500).Take(8))
        {
            var gens = s.m_generators.OfType<hkbClipGenerator>().ToList();
            var ours = gens.First(c => mine.Contains(c));
            var tmpl = gens.FirstOrDefault(c => c.m_animationName is { } n && n.EndsWith(ours.m_animationName![4..]) && c != ours);
            Log($"  selector '{s.m_name}' {s.m_offsetType} animId {s.m_animId}: {s.m_generators.Count} generators; ours '{ours.m_name}' anim {ours.m_animationName} id {ours.m_animationInternalId} mode {ours.m_mode} rate {ours.m_playbackSpeed}" +
                (tmpl is null ? "" : $"; e.g. '{tmpl.m_name}' anim {tmpl.m_animationName} id {tmpl.m_animationInternalId} mode {tmpl.m_mode}"));
        }
        Directory.CreateDirectory(ctx.WorkPath("reports", "explore"));
        File.WriteAllText(ctx.WorkPath("reports", "explore", $"mod-check-{weaponId}.txt"), report.ToString());
        return 0;
    }
}
