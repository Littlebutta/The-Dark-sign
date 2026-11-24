using SoulsFormats;
// taeloop <anibnd.dcx> <entry> loop          : sets that TAE entry's IsLoopByDefault to true, nothing else.
// taeloop <anibnd.dcx> <entry> dropjt <id>   : deletes only that entry's type 0 events whose JumpTable ID is <id>.
TAE.ValidateEventBank = false;
var bnd = BND4.Read(DCX.Decompress(File.ReadAllBytes(args[0]), out var comp));
long id = long.Parse(args[1]);
var mode = args.Length > 2 ? args[2] : "loop";
int jt = mode == "dropjt" ? int.Parse(args[3]) : -1;
int changed = 0;
static string Jts(TAE.Animation a) => string.Join(" ", a.Events.Where(e => e.Type == 0).Select(e => BitConverter.ToInt32(e.GetParameterBytes(false), 0)));
foreach (var f in bnd.Files.Where(f => f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase)))
{
    var tae = TAE.Read(f.Bytes);
    var a = tae.Animations.FirstOrDefault(a => a.ID == id);
    if (a?.MiniHeader is not TAE.Animation.AnimMiniHeader.Standard s) continue;
    if (mode == "loop")
    {
        Console.WriteLine($"{f.Name} entry {id}: IsLoopByDefault {s.IsLoopByDefault} -> True");
        s.IsLoopByDefault = true;
    }
    else
    {
        int before = a.Events.Count;
        Console.WriteLine($"{f.Name} entry {id}: {before} events, JumpTables before: {Jts(a)}");
        a.Events.RemoveAll(e => e.Type == 0 && BitConverter.ToInt32(e.GetParameterBytes(false), 0) == jt);
        Console.WriteLine($"  removed {before - a.Events.Count} JumpTable {jt} event(s); JumpTables after: {Jts(a)}");
    }
    f.Bytes = tae.Write();
    changed++;
}
if (changed == 0) { Console.WriteLine("entry not found"); return 1; }
File.WriteAllBytes(args[0], DCX.Compress(bnd.Write(), comp));
var check = BND4.Read(DCX.Decompress(File.ReadAllBytes(args[0])));
foreach (var f in check.Files.Where(f => f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase)))
    if (TAE.Read(f.Bytes).Animations.FirstOrDefault(a => a.ID == id) is { MiniHeader: TAE.Animation.AnimMiniHeader.Standard s2 } a2)
        Console.WriteLine($"reread: IsLoopByDefault {s2.IsLoopByDefault}, {a2.Events.Count} events, JumpTables {Jts(a2)}");
return 0;
