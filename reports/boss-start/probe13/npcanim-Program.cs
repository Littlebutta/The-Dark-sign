using DarkSign.Porter;
using SoulsFormats;
// npcanim survey <Mods\ER folder>                 every ported NpcParam row (darksign_chr_map.tsv) and its anibnd/model/texture swaps
// npcanim set <Mods\ER folder> <model e.g. c7112> clears those swaps (-1) on that model's rows, saves regulation.bin in place
// (probe 13, 2026-11-24). NpcParam normalChangeAnimChrId, Paramdex: "replaces the character's animations with the
// anibnd of the given ID". The port copies the template's row, so a template that borrows its base's animations
// (c3061 -> c3060) hands that borrowing to the DS3 enemy, and the game plays the base's vanilla clips.
string[] Fields = ["normalChangeAnimChrId", "RetargetReferenceChrId", "normalChangeModelId", "normalChangeTexChrId", "normalChangeResouceId"];
var mod = args[1];
var cache = Path.Combine(Path.GetTempPath(), "darksign-probe13-paramdex");
var reg = ParamStore.Load(PorterGame.EldenRing, mod, new ExternalData(cache));
var npc = reg.Get("NpcParam")!;
var rows = File.ReadLines(Path.Combine(mod, "darksign_chr_map.tsv")).Where(l => !l.StartsWith('#')).Select(l => l.Split('\t'))
    .Where(p => p.Length >= 5 && p[0] is "npc" or "chr" && int.TryParse(p[3], out _)).Select(p => (Model: p[2], Row: int.Parse(p[3]))).Distinct().ToList();
object? Get(PARAM.Row r, string f) => r.Cells.FirstOrDefault(c => c.Def.InternalName == f)?.Value;
if (args[0] == "survey")
{
    int borrowing = 0;
    foreach (var (model, id) in rows.OrderBy(r => r.Model).ThenBy(r => r.Row))
    {
        var r = npc[id];
        if (r is null) { Console.WriteLine($"{model}\t{id}\tMISSING"); continue; }
        var vals = Fields.Select(f => $"{f}={Get(r, f)}").ToList();
        bool b = Convert.ToInt32(Get(r, "normalChangeAnimChrId")) != -1;
        if (b) borrowing++;
        Console.WriteLine($"{model}\t{id}\t{string.Join("\t", vals)}\t{r.Name}{(b ? "\tBORROWS ANIMATIONS" : "")}");
    }
    Console.WriteLine($"{rows.Count} ported rows; {borrowing} take another character's anibnd (normalChangeAnimChrId != -1); models: {rows.Where(x => npc[x.Row] is { } r && Convert.ToInt32(Get(r, "normalChangeAnimChrId")) != -1).Select(x => x.Model).Distinct().Count()}");
    return;
}
var target = args[2];
foreach (var (model, id) in rows.Where(r => r.Model.Equals(target, StringComparison.OrdinalIgnoreCase)))
{
    var r = npc[id]!;
    foreach (var f in Fields)
    {
        var c = r.Cells.First(c => c.Def.InternalName == f);
        Console.WriteLine($"{model} {id} {f}: {c.Value} -> -1");
        c.Value = (short)-1;
    }
}
reg.Save(Path.Combine(mod, "regulation.bin"));
var check = ParamStore.Load(PorterGame.EldenRing, mod, new ExternalData(cache)).Get("NpcParam")!;
foreach (var (model, id) in rows.Where(r => r.Model.Equals(target, StringComparison.OrdinalIgnoreCase)))
    Console.WriteLine($"read back {id}: " + string.Join(" ", Fields.Select(f => $"{f}={Get(check[id]!, f)}")));
