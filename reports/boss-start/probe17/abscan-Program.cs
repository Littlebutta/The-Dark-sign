using SoulsFormats;
using DarkSign.Porter;
// abscan <out dir>: vanilla ER 3[24] uses by ActionButtonParam row, category histogram, and the mod's 74000000-74099999 rows.
var outDir = args[0];
var data = new ExternalData("D:/The Dark sign/TheDarkSign/Work/cache");
var er = GameArchives.Open(PorterGame.EldenRing, "D:/The Dark sign/ELDEN RING/Game", data);
var van = ParamStore.Load(PorterGame.EldenRing, "D:/The Dark sign/ELDEN RING/Game", data).Get("ActionButtonParam")!;
var mod = ParamStore.Load(PorterGame.EldenRing, "D:/The Dark sign/TheDarkSign/Mods/ER", data).Get("ActionButtonParam")!;
object? Get(PARAM.Row r, string f) => r.Cells.FirstOrDefault(c => c.Def.InternalName == f)?.Value;
string Desc(PARAM.Row? r) => r is null ? "MISSING" : $"cat {Get(r, "category")}\ttext {Get(r, "textId")}\tregion {Get(r, "regionType")}\tradius {Get(r, "radius")}\tdmy {Get(r, "dummyPoly1")}\t{r.Name}";
var uses = new Dictionary<int, (int N, Dictionary<string, int> Kinds)>();
var msbCache = new Dictionary<string, MSBE?>();
string Kind(string emevdName, int entity, bool param)
{
    if (param) return "param";
    if (entity == 0) return "none";
    var map = emevdName.Replace(".emevd.dcx", "");
    if (!map.StartsWith("m")) return "common";
    if (!msbCache.TryGetValue(map, out var msb))
    {
        var path = $"/map/mapstudio/{map}.msb.dcx";
        try { msb = er.Contains(path) ? MSBE.Read(er.Read(path)) : null; } catch { msb = null; }
        msbCache[map] = msb;
    }
    if (msb is null) return "nomsb";
    var p = msb.Parts.GetEntries().FirstOrDefault(x => x.EntityID == entity);
    if (p is not null) return p.GetType().Name;
    if (msb.Regions.GetEntries().Any(x => x.EntityID == entity)) return "Region";
    return "unresolved";
}
foreach (var e in er.Entries.Where(e => e.Path is not null && e.Path.StartsWith("/event/") && e.Path.EndsWith(".emevd.dcx")))
{
    EMEVD em;
    try { em = EMEVD.Read(er.Read(e.Path!)); } catch { continue; }
    var name = Path.GetFileName(e.Path!);
    foreach (var ev in em.Events)
        for (int i = 0; i < ev.Instructions.Count; i++)
        {
            var ins = ev.Instructions[i];
            if (ins.Bank != 3 || ins.ID != 24 || ins.ArgData.Length < 12) continue;
            bool btnParam = ev.Parameters.Any(p => p.InstructionIndex == i && p.TargetStartByte == 4);
            bool entParam = ev.Parameters.Any(p => p.InstructionIndex == i && p.TargetStartByte == 8);
            int btn = btnParam ? -1 : BitConverter.ToInt32(ins.ArgData, 4);
            int ent = BitConverter.ToInt32(ins.ArgData, 8);
            if (!uses.TryGetValue(btn, out var u)) uses[btn] = u = (0, new());
            var k = Kind(name, ent, entParam);
            u.Kinds[k] = u.Kinds.GetValueOrDefault(k) + 1;
            uses[btn] = (u.N + 1, u.Kinds);
        }
}
using (var w = new StreamWriter(Path.Combine(outDir, "1-vanilla-3-24-uses.tsv")))
{
    w.WriteLine("row\tuses\ttargets\tcategory\ttextId\tregionType\tradius\tdummyPoly1\tname");
    foreach (var (btn, u) in uses.OrderByDescending(x => x.Value.N))
        w.WriteLine($"{(btn == -1 ? "(param)" : btn)}\t{u.N}\t{string.Join(" ", u.Kinds.Select(k => $"{k.Key}x{k.Value}"))}\t{(btn == -1 ? "-" : Desc(van[btn]))}");
}
using (var w = new StreamWriter(Path.Combine(outDir, "2-vanilla-category-histogram.txt")))
    foreach (var g in van.Rows.GroupBy(r => Convert.ToInt32(Get(r, "category"))).OrderBy(g => g.Key))
        w.WriteLine($"category {g.Key}: {g.Count()} rows; e.g. {string.Join("; ", g.Take(3).Select(r => $"{r.ID} {r.Name} (text {Get(r, "textId")}, region {Get(r, "regionType")}, radius {Get(r, "radius")})"))}");
using (var w = new StreamWriter(Path.Combine(outDir, "3-mod-74000000-74099999.tsv")))
{
    w.WriteLine("row\tcategory\ttextId\tregionType\tradius\tdummyPoly1\tname");
    foreach (var r in mod.Rows.Where(r => r.ID >= 74000000 && r.ID <= 74099999))
        w.WriteLine($"{r.ID}\t{Desc(r)}");
}
Console.WriteLine($"{uses.Count} rows used by 3[24]; {uses.Values.Sum(u => u.N)} uses; vanilla rows {van.Rows.Count}; mod 74xxxxxx rows {mod.Rows.Count(r => r.ID >= 74000000 && r.ID <= 74099999)}");
