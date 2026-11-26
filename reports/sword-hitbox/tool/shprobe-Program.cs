using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SoulsFormats;

// shprobe: read-only dumps for the "sword hitbox" evidence pull (reports/sword-hitbox).
//   emevd <file> <emedf.json>                  every event, instruction names and argument names from the emedf
//   scan <dir> <emedf.json> <out72> <outAnims> DS3 2003[72] with 6 lines of context; 2003[18]/2003[01] on entity 10000
//   tae <anibnd(.dcx)> <tae leaf> <label> <id...>   the given TAE entries, in the layout of the porter's tae-dump
//   msbpart <msb.dcx> <entity id>              the Elden Ring map part(s) with that entity ID
//   bnd <file>                                 file list of a binder
static byte[] Load(string path)
{
    var b = File.ReadAllBytes(path);
    return DCX.Is(b) ? DCX.Decompress(b) : b;
}

var cmd = args[0];
if (cmd == "emevd")
{
    var doc = new Emedf(args[2]);
    var em = EMEVD.Read(Load(args[1]));
    Console.WriteLine($"# {args[1].Replace('\\', '/')} ({em.Events.Count} events), names from {Path.GetFileName(args[2])}");
    foreach (var ev in em.Events)
    {
        Console.WriteLine(Emedf.Header(ev));
        for (int i = 0; i < ev.Instructions.Count; i++)
            Console.WriteLine($"{i,5}: {doc.Line(ev.Instructions[i])}");
    }
}
else if (cmd == "scan")
{
    var doc = new Emedf(args[2]);
    var o72 = new StringBuilder();
    var oan = new StringBuilder();
    o72.AppendLine($"# DS3 bank 2003 id 72 in every event script under {args[1].Replace('\\', '/')}, names from {Path.GetFileName(args[2])}");
    o72.AppendLine("# each hit: file, event ID, instruction index, then the event's lines from 6 before to 6 after (the hit is marked >>).");
    o72.AppendLine("# an argument that the event takes from its caller shows the placeholder stored in the file (usually 0); the event's 'params [...]' list says which bytes are filled in (iN = instruction index, tN = byte offset in its arguments).");
    oan.AppendLine($"# DS3 bank 2003 id 18 / id 01 (Force Animation Playback) on Entity ID 10000 (the player) in every event script under {args[1].Replace('\\', '/')}");
    oan.AppendLine("# columns: file\tevent\tline\tinstruction\tanimation ID\tfull line");
    var par = new StringBuilder();
    par.AppendLine();
    par.AppendLine("# the same instructions whose Entity ID or Animation ID comes from an event parameter (the caller supplies it); the callers that pass 10000 anywhere in their parameters are listed under each");
    par.AppendLine("# columns: file\tevent\tline\tinstruction\twhich arguments are parameters\tfull line");
    int n72 = 0, nan = 0, npar = 0;
    var files = Directory.GetFiles(args[1], "*.emevd*").OrderBy(f => f).ToList();
    var all = files.Select(f => (File: Path.GetFileName(f), Em: EMEVD.Read(Load(f)))).ToList();
    foreach (var (file, em) in all)
    {
        foreach (var ev in em.Events)
            for (int i = 0; i < ev.Instructions.Count; i++)
            {
                var ins = ev.Instructions[i];
                if (ins.Bank != 2003) continue;
                if (ins.ID == 72)
                {
                    n72++;
                    o72.AppendLine();
                    o72.AppendLine($"== {file} event {ev.ID} line {i}");
                    o72.AppendLine(Emedf.Header(ev));
                    for (int k = Math.Max(0, i - 6); k <= Math.Min(ev.Instructions.Count - 1, i + 6); k++)
                        o72.AppendLine($"{(k == i ? ">>" : "  ")}{k,5}: {doc.Line(ev.Instructions[k])}");
                    // who starts this event, and with what
                    foreach (var (f2, em2) in all)
                        foreach (var ev2 in em2.Events)
                            for (int k = 0; k < ev2.Instructions.Count; k++)
                            {
                                var c = ev2.Instructions[k];
                                if (c.Bank == 2000 && (c.ID == 0 || c.ID == 6) && c.ArgData.Length >= 8 && BitConverter.ToUInt32(c.ArgData, 4) == ev.ID
                                    && (c.ID == 0 ? f2 == file : true))
                                    o72.AppendLine($"   started by {f2} event {ev2.ID} line {k}: {doc.Line(c)}");
                            }
                }
                if ((ins.ID == 18 || ins.ID == 1) && ins.ArgData.Length >= 8)
                {
                    uint entity = BitConverter.ToUInt32(ins.ArgData, 0);
                    int anim = BitConverter.ToInt32(ins.ArgData, 4);
                    var ps = ev.Parameters.Where(p => p.InstructionIndex == i && p.TargetStartByte < 8).ToList();
                    if (entity == 10000)
                    {
                        nan++;
                        oan.AppendLine($"{file}\t{ev.ID}\t{i}\t2003[{ins.ID:D2}]\t{anim}{(ps.Any(p => p.TargetStartByte == 4) ? " (parameter)" : "")}\t{doc.Line(ins)}");
                    }
                    else if (ps.Any(p => p.TargetStartByte == 0))
                    {
                        // callers passing 10000
                        var callers = new List<string>();
                        foreach (var (f2, em2) in all)
                            foreach (var ev2 in em2.Events)
                                for (int k = 0; k < ev2.Instructions.Count; k++)
                                {
                                    var c = ev2.Instructions[k];
                                    if (c.Bank != 2000 || (c.ID != 0 && c.ID != 6) || c.ArgData.Length < 12 || BitConverter.ToUInt32(c.ArgData, 4) != ev.ID) continue;
                                    if (c.ID == 0 && f2 != file) continue;
                                    var src = ps.First(p => p.TargetStartByte == 0);
                                    int off = 8 + (int)src.SourceStartByte;
                                    if (off + 4 <= c.ArgData.Length && BitConverter.ToUInt32(c.ArgData, off) == 10000)
                                        callers.Add($"   caller passes entity 10000: {f2} event {ev2.ID} line {k}: {doc.Line(c)}");
                                }
                        if (callers.Count > 0)
                        {
                            npar++;
                            par.AppendLine($"{file}\t{ev.ID}\t{i}\t2003[{ins.ID:D2}]\t{string.Join(",", ps.Select(p => p.TargetStartByte == 0 ? "Entity ID" : "Animation ID"))}\t{doc.Line(ins)}");
                            foreach (var c in callers) par.AppendLine(c);
                        }
                    }
                }
            }
    }
    o72.Insert(0, $"# {n72} instruction(s) found in {files.Count} files: {string.Join(" ", files.Select(Path.GetFileName))}\n");
    oan.Insert(0, $"# {nan} direct, {npar} through an event parameter; {files.Count} files read\n");
    File.WriteAllText(args[3], o72.ToString());
    File.WriteAllText(args[4], oan.ToString() + par.ToString());
    Console.WriteLine($"2003[72]: {n72}; player anims: {nan} direct, {npar} by parameter; {files.Count} files");
}
else if (cmd == "tae")
{
    TAE.ValidateEventBank = false;
    var bnd = BND4.Read(Load(args[1]));
    var leaves = args[2].Split(',');
    var ids = args.Skip(4).Select(long.Parse).ToList();
    var file = bnd.Files.FirstOrDefault(f => leaves.Any(l => f.Name.EndsWith("\\" + l, StringComparison.OrdinalIgnoreCase)));
    if (file is null) { Console.Error.WriteLine($"no {args[2]} in {args[1]}"); return 1; }
    var tae = TAE.Read(file.Bytes);
    Console.WriteLine($"# {args[3]} {args[1].Replace('\\', '/')} {file.Name}: id {tae.ID}, {tae.Animations.Count} animations; sections kept: {string.Join(" ", ids)}");
    foreach (var id in ids)
    {
        var found = tae.Animations.Where(a => a.ID == id).ToList();
        if (found.Count == 0) { Console.WriteLine($"\n## {id} NOT IN THIS TAE"); continue; }
        foreach (var anim in found)
        {
            var header = anim.MiniHeader is { } h ? $" mini {h.GetType().Name}" : "";
            string extra = anim.MiniHeader switch
            {
                TAE.Animation.AnimMiniHeader.ImportOtherAnim imp => $" (plays entry {imp.ImportFromAnimID})",
                TAE.Animation.AnimMiniHeader.Standard { ImportsHKX: true } s => $" (animation file of {s.ImportHKXSourceAnimID})",
                _ => "",
            };
            Console.WriteLine($"\n## {anim.ID}{header} file '{anim.AnimFileName}', {anim.Events.Count} events{extra}");
            foreach (var ev in anim.Events.OrderBy(e => e.StartTime))
                Console.WriteLine($"  {ev.Type,4} {ev.StartTime,7:0.000}-{ev.EndTime,7:0.000}  {Convert.ToHexString(ev.GetParameterBytes(false))}");
        }
    }
    // which binder files exist for those ids (animation clips)
    Console.WriteLine();
    Console.WriteLine("# animation files in this binder whose name holds one of those IDs:");
    foreach (var f in bnd.Files.Where(f => ids.Any(id => Regex.IsMatch(Path.GetFileName(f.Name.Replace('\\', '/')), $@"^a0*0_0*{id}\."))))
        Console.WriteLine($"#   [{f.ID}] {f.Name} {f.Bytes.Length} bytes");
}
else if (cmd == "msbpart")
{
    var msb = MSBE.Read(Load(args[1]));
    uint id = uint.Parse(args[2]);
    foreach (var p in msb.Parts.GetEntries().Where(p => p.EntityID == id || p.EntityGroupIDs.Contains(id)))
        Console.WriteLine($"{p.GetType().Name}\tname {p.Name}\tmodel {p.ModelName}\tentity {p.EntityID}\tpos {p.Position.X:0.###} {p.Position.Y:0.###} {p.Position.Z:0.###}\trot {p.Rotation.X:0.###} {p.Rotation.Y:0.###} {p.Rotation.Z:0.###}\tgroups {string.Join(",", p.EntityGroupIDs.Where(g => g != 0))}");
}
else if (cmd == "undcx")
{
    File.WriteAllBytes(args[2], Load(args[1]));
}
else if (cmd == "bnd")
{
    var bnd = BND4.Read(Load(args[1]));
    foreach (var f in bnd.Files) Console.WriteLine($"[{f.ID}] {f.Name} {f.Bytes.Length}");
}
return 0;

class Emedf
{
    readonly Dictionary<(int, int), (string Name, List<(string Name, int Type, bool Vararg)> Args)> defs = new();
    public Emedf(string path)
    {
        var text = File.ReadAllText(path);
        JsonDocument doc;
        var opt = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
        try { doc = JsonDocument.Parse(text, opt); }
        catch (JsonException)
        {
            // DarkScript3's DS3 file writes some numbers as ".5" / "1."
            text = Regex.Replace(text, @"(?<=[:\[,]\s*)(-?)\.(\d)", "${1}0.$2");
            text = Regex.Replace(text, @"(\d)\.(?=\s*[,\}\]])", "$1.0");
            doc = JsonDocument.Parse(text, opt);
        }
        foreach (var c in doc.RootElement.GetProperty("main_classes").EnumerateArray())
            foreach (var ins in c.GetProperty("instrs").EnumerateArray())
            {
                var a = new List<(string, int, bool)>();
                foreach (var arg in ins.GetProperty("args").EnumerateArray())
                    a.Add((arg.GetProperty("name").GetString(), arg.GetProperty("type").GetInt32(),
                        arg.TryGetProperty("vararg", out var v) && v.ValueKind == JsonValueKind.True));
                defs[(c.GetProperty("index").GetInt32(), ins.GetProperty("index").GetInt32())] = (ins.GetProperty("name").GetString(), a);
            }
    }
    public static string Header(EMEVD.Event ev) =>
        $"event {ev.ID} rest {ev.RestBehavior} params [{string.Join(" ", ev.Parameters.Select(p => $"i{p.InstructionIndex}:t{p.TargetStartByte}<-s{p.SourceStartByte}/{p.ByteCount}"))}]";
    static string Words(byte[] d, int from)
    {
        var parts = new List<string>();
        int i = from;
        for (; i + 4 <= d.Length; i += 4) parts.Add(BitConverter.ToInt32(d, i).ToString());
        for (; i < d.Length; i++) parts.Add($"b{d[i]}");
        return string.Join(" ", parts);
    }
    public string Line(EMEVD.Instruction ins)
    {
        var d = ins.ArgData;
        if (!defs.TryGetValue((ins.Bank, ins.ID), out var def))
            return $"{ins.Bank}[{ins.ID:D2}] ?: raw {Words(d, 0)}";
        var parts = new List<string>();
        int o = 0;
        foreach (var (name, type, vararg) in def.Args)
        {
            if (vararg) { o = (o + 3) & ~3; parts.Add($"{name}={Words(d, o)}"); o = d.Length; break; }
            int size = type switch { 0 or 3 => 1, 1 or 4 => 2, _ => 4 };
            o = (o + size - 1) & ~(size - 1);
            if (o + size > d.Length) { parts.Add($"{name}=<missing>"); continue; }
            // floats are shown as their raw 32-bit pattern (as in reports/boss-start) followed by the value
            string val = type switch
            {
                0 => d[o].ToString(),
                3 => ((sbyte)d[o]).ToString(),
                1 => BitConverter.ToUInt16(d, o).ToString(),
                4 => BitConverter.ToInt16(d, o).ToString(),
                2 => BitConverter.ToUInt32(d, o).ToString(),
                6 => FormattableString.Invariant($"{BitConverter.ToInt32(d, o)}({BitConverter.ToSingle(d, o):0.###}f)"),
                _ => BitConverter.ToInt32(d, o).ToString(),
            };
            parts.Add($"{name}={val}");
            o += size;
        }
        return $"{ins.Bank}[{ins.ID:D2}] {def.Name}: {string.Join(", ", parts)}";
    }
}
