using System.Text.RegularExpressions;
using SoulsFormats;
// taeid survey <Mods\ER\chr folder>          lists every anibnd TAE's ID and sib name, and which share one
// taeid set <cXXXX.anibnd.dcx>               gives that character's TAEs its own identity: ID 200000 + XXXX, cXXXX.sib
// (probe 10, 2026-11-24: does the game take an enemy's animations from the character its TAE ID / sib names?)
TAE.ValidateEventBank = false;
if (args[0] == "mk") // offline self-test only: a main anibnd holding one TAE
{
    var b = new BND4();
    b.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 5000000, @"N:\GR\data\INTERROOT_win64\chr\c7112\tae\c7112.tae", File.ReadAllBytes(args[1])));
    File.WriteAllBytes(args[2], DCX.Compress(b.Write(), new DCX.DcxDfltCompressionInfo(DCX.DfltCompressionPreset.DCX_DFLT_11000_44_9)));
    return;
}
if (args[0] == "mkdiv") // offline self-test only: a div binder from loose files
{
    var b = new BND4(); int i = 0;
    foreach (var f in args.Skip(2)) b.Files.Add(new BinderFile(Binder.FileFlags.Flag1, f.EndsWith(".compendium") ? 7000000 : 1000000000 + i++, @"N:\GR\data\INTERROOT_win64\chr\c7112\hkx_div00_compendium\" + Leaf(f), File.ReadAllBytes(f)));
    File.WriteAllBytes(args[1], DCX.Compress(b.Write(), new DCX.DcxDfltCompressionInfo(DCX.DfltCompressionPreset.DCX_DFLT_11000_44_9)));
    return;
}
if (args[0] == "clips") // taeid clips <anibnd(.dcx) or folder of extracted div binders> <id>...: each clip's length
{
    var want = args.Skip(2).Select(int.Parse).ToHashSet();
    var paths = Directory.Exists(args[1]) ? Directory.GetFiles(args[1], "*.anibnd*").Order().ToArray() : [args[1]];
    foreach (var path in paths)
    {
        var bytes = File.ReadAllBytes(path);
        BND4 bnd;
        try { bnd = BND4.Read(DCX.Is(bytes) ? DCX.Decompress(bytes) : bytes); } catch (Exception ex) { Console.WriteLine($"{Leaf(path)}: unreadable ({ex.Message})"); continue; }
        var cmp = bnd.Files.FirstOrDefault(f => f.Name.EndsWith(".compendium", StringComparison.OrdinalIgnoreCase))?.Bytes.ToArray();
        foreach (var f in bnd.Files)
        {
            var m = Regex.Match(Leaf(f.Name), @"^a(\d{3})_(\d{6})\.hkx$", RegexOptions.IgnoreCase);
            if (!m.Success || !want.Contains(int.Parse(m.Groups[2].Value))) continue;
            try
            {
                var ser = new HKLib.Serialization.hk2018.Binary.HavokBinarySerializer();
                if (cmp is not null) ser.LoadCompendium(new MemoryStream(cmp));
                var root = (HKLib.hk2018.hkRootLevelContainer)ser.Read(new MemoryStream(f.Bytes.ToArray()));
                var c = root.m_namedVariants.Select(v => v.m_variant).OfType<HKLib.hk2018.hkaAnimationContainer>().First();
                var a = c.m_animations[0];
                Console.WriteLine($"{Leaf(path)}\t{f.ID}\t{Leaf(f.Name)}\t{a.m_duration:0.###} s\t{a.m_numberOfTransformTracks} tracks\t{f.Bytes.Length} bytes");
            }
            catch (Exception ex) { Console.WriteLine($"{Leaf(path)}\t{f.ID}\t{Leaf(f.Name)}\tunreadable ({ex.Message})"); }
        }
    }
    return;
}
if (args[0] == "swap") // probe 11: taeid swap <cXXXX_divNN.anibnd.dcx> <target id> <source id>: target clip's bytes := source clip's
{
    var raw0 = File.ReadAllBytes(args[1]);
    var div = BND4.Read(DCX.Decompress(raw0, out var divComp));
    BinderFile Clip(string id) => div.Files.Single(f => Leaf(f.Name).Equals($"a000_{int.Parse(id):D6}.hkx", StringComparison.OrdinalIgnoreCase));
    var target = Clip(args[2]); var source = Clip(args[3]);
    Console.WriteLine($"{Leaf(target.Name)} ({target.Bytes.Length} bytes) now holds {Leaf(source.Name)} ({source.Bytes.Length} bytes)");
    target.Bytes = source.Bytes.ToArray();
    File.WriteAllBytes(args[1], DCX.Compress(div.Write(), divComp));
    return;
}
if (args[0] == "survey")
{
    var rows = new List<(string Chr, string Tae, int Id, string Sib)>();
    foreach (var path in Directory.GetFiles(args[1], "c*.anibnd.dcx").Where(p => Regex.IsMatch(Path.GetFileName(p), @"^c\d{4}\.anibnd\.dcx$", RegexOptions.IgnoreCase)).Order())
    {
        BND4 bnd;
        try { bnd = BND4.Read(DCX.Decompress(File.ReadAllBytes(path))); } catch (Exception ex) { Console.WriteLine($"{Leaf(path)}: unreadable ({ex.Message})"); continue; }
        foreach (var f in bnd.Files.Where(f => f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase)))
        {
            try { var t = TAE.Read(f.Bytes); rows.Add((Leaf(path)[..5], Leaf(f.Name), t.ID, t.SibName)); }
            catch (Exception ex) { Console.WriteLine($"{Leaf(path)} {Leaf(f.Name)}: unreadable TAE ({ex.Message})"); }
        }
    }
    foreach (var r in rows) Console.WriteLine($"{r.Chr}\t{r.Tae}\t{r.Id}\t{r.Sib}\t{(r.Id == 200000 + int.Parse(r.Chr[1..]) ? "own" : "FOREIGN")}");
    Console.WriteLine($"{rows.Count} TAEs; {rows.Count(r => r.Id != 200000 + int.Parse(r.Chr[1..]))} carry another character's ID; IDs shared by more than one character: " +
        string.Join(", ", rows.GroupBy(r => r.Id).Where(g => g.Select(r => r.Chr).Distinct().Count() > 1).Select(g => $"{g.Key} x{g.Select(r => r.Chr).Distinct().Count()}")));
    return;
}
var file = args[1];
int chr = int.Parse(Regex.Match(Leaf(file), @"c(\d{4})").Groups[1].Value);
var raw = File.ReadAllBytes(file);
var main = BND4.Read(DCX.Decompress(raw, out var comp));
foreach (var f in main.Files.Where(f => f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase)))
{
    var t = TAE.Read(f.Bytes);
    Console.WriteLine($"{Leaf(f.Name)}: ID {t.ID} sib {t.SibName} ({t.Animations.Count} entries)");
    t.ID = 200000 + chr;
    t.SibName = $"c{chr:D4}.sib";
    f.Bytes = t.Write();
    var back = TAE.Read(f.Bytes);
    Console.WriteLine($"  now: ID {back.ID} sib {back.SibName} ({back.Animations.Count} entries)");
}
File.WriteAllBytes(file, DCX.Compress(main.Write(), comp));

static string Leaf(string p) => p[(p.LastIndexOfAny(['\\', '/']) + 1)..];
