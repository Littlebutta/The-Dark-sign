using SoulsFormats;
// taeswap <anibnd.dcx> <op>...   (probe 5, 2026-11-24; edits only the section-0 TAE in c7112.anibnd.dcx)
//   events:<dst>=<src>   dst entry's events become a copy of src entry's events (file name / header unchanged)
//   file:<dst>=<clipId>  dst entry plays a000_<clipId>.hkt (own file, ImportsHKX off), events unchanged
//   blendonly:<dst>      dst entry keeps only its type 16 (Blend) events
// Prints every touched entry before and after, then re-reads the written file.
TAE.ValidateEventBank = false;
var raw0 = File.ReadAllBytes(args[0]);
bool isDcx = DCX.Is(raw0);
DCX.CompressionInfo comp = null;
var bnd = BND4.Read(isDcx ? DCX.Decompress(raw0, out comp) : raw0);
var f = bnd.Files.First(x => x.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase) && x.Name.Contains("c7112", StringComparison.OrdinalIgnoreCase));
var tae = TAE.Read(f.Bytes);
TAE.Animation E(long id) => tae.Animations.First(a => a.ID == id);
static string Show(TAE.Animation a) => $"{a.ID}: file {a.AnimFileName} " +
    (a.MiniHeader is TAE.Animation.AnimMiniHeader.Standard s ? $"imports {s.ImportsHKX} src {s.ImportHKXSourceAnimID}" : a.MiniHeader?.GetType().Name) +
    $", {a.Events.Count} events: " + string.Join(" ", a.Events.Select(e => $"t{e.Type}[{e.StartTime:0.###}-{e.EndTime:0.###}]"));
foreach (var op in args.Skip(1))
{
    var (kind, rest) = (op[..op.IndexOf(':')], op[(op.IndexOf(':') + 1)..]);
    var dst = E(long.Parse(rest.Split('=')[0]));
    Console.WriteLine($"{op}\n  before {Show(dst)}");
    switch (kind)
    {
        case "events":
            var src = E(long.Parse(rest.Split('=')[1]));
            dst.Events.Clear();
            dst.EventGroups.Clear();
            foreach (var e in src.Events)
                dst.Events.Add(new TAE.Event(e.StartTime, e.EndTime, e.Type, e.Unk04, (byte[])e.GetParameterBytes(false).Clone(), false));
            break;
        case "file":
            int clip = int.Parse(rest.Split('=')[1]);
            if (dst.MiniHeader is TAE.Animation.AnimMiniHeader.Standard s) { s.ImportsHKX = false; s.ImportHKXSourceAnimID = -1; }
            else dst.MiniHeader = new TAE.Animation.AnimMiniHeader.Standard();
            dst.AnimFileName = $"a000_{clip:D6}.hkt";
            break;
        case "blendonly":
            dst.Events.RemoveAll(e => e.Type != 16);
            foreach (var e in dst.Events) e.Group = null;
            dst.EventGroups.Clear();
            break;
        default: throw new ArgumentException(op);
    }
    Console.WriteLine($"  after  {Show(dst)}");
}
f.Bytes = tae.Write();
File.WriteAllBytes(args[0], isDcx ? DCX.Compress(bnd.Write(), comp) : bnd.Write());
var raw1 = File.ReadAllBytes(args[0]);
var check = TAE.Read(BND4.Read(isDcx ? DCX.Decompress(raw1) : raw1).Files.First(x => x.Name == f.Name).Bytes);
foreach (var op in args.Skip(1))
    Console.WriteLine("reread " + Show(check.Animations.First(a => a.ID == long.Parse(op[(op.IndexOf(':') + 1)..].Split('=')[0]))));
return 0;
