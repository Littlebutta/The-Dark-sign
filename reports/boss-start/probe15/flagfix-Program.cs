using SoulsFormats;
// flagfix <in common.emevd.dcx> <out>: in every event that waits on flag 1039292400 and warps (2003[14]), adds
// 2003[66] Set Event Flag (type 0, flag 45000802, OFF) right before the warp, copying an existing 2003[66]'s layout.
var raw = DCX.Decompress(File.ReadAllBytes(args[0]), out var comp);
var em = EMEVD.Read(raw);
var tmpl = em.Events.SelectMany(e => e.Instructions).First(i => i.Bank == 2003 && i.ID == 66);
Console.WriteLine($"2003[66] template args {Convert.ToHexString(tmpl.ArgData)}");
bool Waits(EMEVD.Event e) => e.Instructions.Any(i => i.ArgData.Length >= 8 && Enumerable.Range(0, i.ArgData.Length / 4).Any(k => BitConverter.ToUInt32(i.ArgData, k * 4) == 1039292400u));
int n = 0;
foreach (var ev in em.Events.Where(Waits))
{
    int w = ev.Instructions.FindIndex(i => i.Bank == 2003 && i.ID == 14);
    if (w < 0) continue;
    var a = (byte[])tmpl.ArgData.Clone();
    a[0] = 0; // type 0 (event flag)
    BitConverter.TryWriteBytes(a.AsSpan(4), 45000802u);
    a[8] = 0; // OFF
    ev.Instructions.Insert(w, new EMEVD.Instruction(2003, 66, a));
    // Parameter replacements after the insert point shift by one instruction.
    foreach (var p in ev.Parameters.Where(p => p.InstructionIndex >= w)) p.InstructionIndex++;
    Console.WriteLine($"event {ev.ID}: inserted at {w}: {Convert.ToHexString(a)} (warp {Convert.ToHexString(ev.Instructions[w + 1].ArgData)})");
    n++;
}
Console.WriteLine($"{n} event(s) changed");
File.WriteAllBytes(args[1], DCX.Compress(em.Write(), comp));
